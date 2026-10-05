using System.Globalization;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Extensions;

/// <summary>A run of message text: plain, or a hashtag (<see cref="Tag"/> lower case, to look it up).</summary>
public sealed record TextPart(string Text, string? Tag)
{
    public bool IsTag => Tag is not null;
}

/// <summary>A photo or video in a message, with full addresses (the server sends them relative to the site).</summary>
public sealed record MediaItemViewModel(MediaItem Item, string Url, string ThumbUrl)
{
    public bool IsVideo => Item.Type == MediaTypes.Video;

    public static MediaItemViewModel From(MediaItem item, Uri site) =>
        new(item, new Uri(site, item.Url).ToString(), new Uri(site, item.ThumbUrl ?? item.Url).ToString());
}

/// <summary>Photos and videos sent together: one bubble, laid out as a gallery (SRS §3.3).</summary>
public sealed class MediaMessageViewModel : MessageViewModel
{
    readonly Uri _site;

    public MediaMessageViewModel(MessageDto message, MessageRenderContext context, Uri site) : base(message, context)
    {
        _site = site;
        Load();
    }

    public IReadOnlyList<MediaItemViewModel> Items { get; private set; } = [];
    public string? Caption { get; private set; }
    public IReadOnlyList<TextPart> CaptionParts { get; private set; } = [];
    public bool HasCaption => !string.IsNullOrEmpty(Caption);
    public bool Forwarded { get; private set; }
    public override bool IsForwarded => Forwarded;

    /// <summary>One wide tile for a single item, otherwise two to a row.</summary>
    public int Columns => Items.Count == 1 ? 1 : 2;

    public override string Preview
    {
        get
        {
            var videos = Items.Count(i => i.IsVideo);
            var photos = Items.Count - videos;
            var what = (photos, videos) switch
            {
                (1, 0) => "📷 Photo",
                (_, 0) => $"📷 {photos} photos",
                (0, 1) => "🎥 Video",
                (0, _) => $"🎥 {videos} videos",
                _ => $"📷 {Items.Count} photos and videos"
            };
            return HasCaption ? $"{what} · {Caption}" : what;
        }
    }

    void Load()
    {
        var payload = ContractJson.Read<MediaPayload>(Message.Payload);
        Items = (payload?.Items ?? []).Select(i => MediaItemViewModel.From(i, _site)).ToList();
        Caption = payload?.Caption;
        CaptionParts = Hashtags.Split(Caption).Select(p => new TextPart(p.Text, p.Tag)).ToList();
        Forwarded = payload?.Forwarded == true;
    }

    protected override void OnConfirmed()
    {
        Load();
        OnPropertyChanged(string.Empty);
    }
}

public sealed class MediaMessageRenderer(ClientOptions options) : IMessageRenderer
{
    public string Kind => MessageKinds.Media;
    public MessageViewModel Create(MessageDto message, MessageRenderContext context) => new MediaMessageViewModel(message, context, options.ApiBaseUrl);
}

/// <summary>Livestock for sale, as a card in the chat (SRS §3.4). Its status follows the listing everywhere it's shown.</summary>
public sealed partial class ListingMessageViewModel : MessageViewModel
{
    readonly Uri _site;
    readonly Guid _myUserId;

    public ListingMessageViewModel(MessageDto message, MessageRenderContext context, Uri site) : base(message, context)
    {
        (_site, _myUserId) = (site, context.MyUserId);
        Listing = Read();
        Status = Listing.Status;
    }

    public MarketListingPayload Listing { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsOpen), nameof(CanOffer), nameof(StatusTone))]
    public partial string Status { get; set; }

    public Guid ListingId => Listing.ListingId;
    public bool IsSeller => Listing.SellerId == _myUserId;
    public string Title => Listing.Title;
    public string Icon => MarketContract.SpeciesIcon(Listing.Species);
    public string PriceText => MarketContract.Price(Listing.Price, Listing.Currency);
    public string? Description => Listing.Description;
    public bool HasDescription => !string.IsNullOrEmpty(Listing.Description);
    public IReadOnlyList<TextPart> DescriptionParts => Hashtags.Split(Listing.Description).Select(p => new TextPart(p.Text, p.Tag)).ToList();

    /// <summary>"3 · Boer goats · 5 years old · Monze".</summary>
    public string Details => string.Join(" · ", new[]
    {
        Listing.Quantity > 1 ? Listing.Quantity.ToString("N0", CultureInfo.InvariantCulture) : null,
        string.Join(' ', new[] { Listing.Breed, MarketContract.SpeciesLabel(Listing.Species).ToLowerInvariant() }.Where(s => !string.IsNullOrEmpty(s))),
        Listing.Age,
        Listing.Location is { } place ? "📍 " + place : null
    }.Where(s => !string.IsNullOrEmpty(s)));

    public string? BarterText => Listing.OpenToBarter ? "🔁 Open to swaps" + (Listing.BarterTerms is { } terms ? $": {terms}" : "") : null;
    public bool HasBarter => BarterText is not null;
    public string StatusText => ListingStatuses.Label(Status);
    public string StatusTone => Status switch { ListingStatuses.Available => "ok", ListingStatuses.Upcoming => "wait", _ => "bad" };
    public bool IsOpen => ListingStatuses.IsOpen(Status);

    /// <summary>Someone else can make an offer here while it's open.</summary>
    public bool CanOffer => IsOpen && !IsSeller && Delivery == DeliveryStatus.Sent;

    public IReadOnlyList<MediaItemViewModel> Media => Listing.Media.Select(m => MediaItemViewModel.From(m, _site)).ToList();
    public string? CoverUrl => Media.FirstOrDefault(m => !m.IsVideo)?.ThumbUrl;
    public bool HasCover => CoverUrl is not null;
    public int MoreMedia => Math.Max(0, Listing.Media.Count - 1);
    public override bool IsForwarded => Listing.Forwarded;

    public override string Preview => $"{Icon} {(Status == ListingStatuses.Upcoming ? "Coming soon" : "For sale")}: {Title} · {PriceText}";

    MarketListingPayload Read() => ContractJson.Read<MarketListingPayload>(Message.Payload) ?? throw new JsonException("Empty listing.");

    public override void ApplyState(JsonElement state)
    {
        if (ContractJson.Read<ListingState>(state)?.Status is { } status)
            Status = status;
    }

    protected override void OnConfirmed()
    {
        Listing = Read();
        Status = Listing.Status;
        OnPropertyChanged(string.Empty);
    }
}

public sealed class ListingMessageRenderer(ClientOptions options) : IMessageRenderer
{
    public string Kind => MessageKinds.MarketListing;
    public MessageViewModel Create(MessageDto message, MessageRenderContext context) => new ListingMessageViewModel(message, context, options.ApiBaseUrl);
}

/// <summary>An offer on a listing: money or a trade. The seller answers it here; the buyer can withdraw it.</summary>
public sealed partial class OfferMessageViewModel : MessageViewModel
{
    readonly Guid _myUserId;

    public OfferMessageViewModel(MessageDto message, MessageRenderContext context) : base(message, context)
    {
        _myUserId = context.MyUserId;
        Offer = Read();
        Status = OfferStatuses.Pending;
    }

    public MarketOfferPayload Offer { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(CanAnswer), nameof(CanWithdraw), nameof(IsPending), nameof(StatusTone))]
    public partial string Status { get; set; }

    public Guid OfferId => Offer.OfferId;
    public bool IsSeller => Offer.SellerId == _myUserId;
    public bool IsPending => Status == OfferStatuses.Pending;

    /// <summary>The seller, while it's waiting.</summary>
    public bool CanAnswer => IsSeller && IsPending && Delivery == DeliveryStatus.Sent;

    /// <summary>The buyer, while it's waiting.</summary>
    public bool CanWithdraw => IsMine && IsPending && Delivery == DeliveryStatus.Sent;

    public string Headline => (IsMine ? "Your offer" : $"{SenderName}'s offer") + $" on {Offer.ListingTitle}";
    public bool IsTrade => Offer.Kind == OfferKinds.Barter;
    public string AmountText => IsTrade ? "🔁 Trade" : MarketContract.Price(Offer.Amount, Offer.Currency ?? MarketContract.DefaultCurrency);
    public string? Text => Offer.Text;
    public bool HasText => !string.IsNullOrEmpty(Offer.Text);
    public string StatusText => OfferStatuses.Label(Status);
    public string StatusTone => Status switch { OfferStatuses.Accepted => "ok", OfferStatuses.Pending => "wait", _ => "bad" };

    public override string Preview => $"🤝 {(IsMine ? "You offered" : $"{SenderName} offered")} {(IsTrade ? "a trade" : AmountText)} for {Offer.ListingTitle}";

    MarketOfferPayload Read() => ContractJson.Read<MarketOfferPayload>(Message.Payload) ?? throw new JsonException("Empty offer.");

    public override void ApplyState(JsonElement state)
    {
        if (ContractJson.Read<OfferState>(state)?.Status is { } status)
            Status = status;
    }

    protected override void OnConfirmed()
    {
        Offer = Read();
        OnPropertyChanged(string.Empty);
    }
}

public sealed class OfferMessageRenderer : IMessageRenderer
{
    public string Kind => MessageKinds.MarketOffer;
    public MessageViewModel Create(MessageDto message, MessageRenderContext context) => new OfferMessageViewModel(message, context);
}

/// <summary>"Sell livestock" in a chat's "+" panel: the Market's sell form, posting into this chat.</summary>
public sealed class SellLivestockAction(INavigator navigator, NdeipiChat.Client.ViewModels.LauncherViewModel launcher) : IComposerAction
{
    public string Title => "Sell livestock";
    public string Glyph => "🐐";
    public int Order => 25;

    public bool IsAvailable(ConversationDto conversation) => launcher.Allows(MarketContract.AppId);

    public Task ExecuteAsync(ComposerContext context) => MarketNavigation.OpenAsync(navigator, $"sell={context.Conversation.Id:N}");
}

/// <summary>Opens the Market app at a listing ("listing={id:N}"), a hashtag ("tag=goats") or the sell form.</summary>
public static class MarketNavigation
{
    public static Task OpenAsync(INavigator navigator, string query) =>
        navigator.GoToAsync(Routes.SubApp(MarketContract.AppId), new Dictionary<string, object>
        {
            [Routes.SubAppRouteParameter] = $"apps/{MarketContract.AppId}?{query}"
        });

    public static Task OpenListingAsync(INavigator navigator, Guid listingId) => OpenAsync(navigator, $"listing={listingId:N}");

    public static Task OpenTagAsync(INavigator navigator, string tag) => OpenAsync(navigator, $"tag={Uri.EscapeDataString(tag)}");
}
