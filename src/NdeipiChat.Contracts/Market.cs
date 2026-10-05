using System.Globalization;
using System.Text.RegularExpressions;

namespace NdeipiChat.Contracts;

/// <summary>
/// The livestock marketplace (SRS "Livestock Networking, Marketplace, and Financial Micro App",
/// phase 1): photos and videos in chats (§3.3), hashtags and forwarding (§3.2), and livestock sale
/// listings and barter or cash offers posted in group chats (§3.4), which the Market app gathers
/// across the groups someone is in. Prices are shown, not paid: buyer and seller agree in chat.
/// </summary>
public static class MarketContract
{
    public const string AppId = "market";
    public const string BasePath = "api/market";

    /// <summary>What listing prices may be in: Zambian kwacha (the default) or US dollars.</summary>
    public static readonly IReadOnlyList<string> Currencies = ["ZMW", "USD"];
    public const string DefaultCurrency = "ZMW";

    /// <summary>Photos and videos in one message or listing.</summary>
    public const int MaxMedia = 10;

    /// <summary>A photo as uploaded (it's resized and recompressed on the server).</summary>
    public const long MaxImageBytes = 25 * 1024 * 1024;

    /// <summary>A video as uploaded. Videos aren't re-encoded, so phones should send them at a lower quality.</summary>
    public const long MaxVideoBytes = 60 * 1024 * 1024;

    public const int MaxTitleLength = 100;
    public const int MaxDescriptionLength = 2000;
    public const int MaxCaptionLength = 2000;
    public const int MaxOfferLength = 500;

    /// <summary>The kinds of animal a listing can be for.</summary>
    public static readonly IReadOnlyList<FinanceOption> Species =
    [
        new("goats", "Goats"),
        new("cattle", "Cattle"),
        new("sheep", "Sheep"),
        new("pigs", "Pigs"),
        new("chickens", "Chickens"),
        new("ducks", "Ducks"),
        new("turkeys", "Turkeys"),
        new("rabbits", "Rabbits"),
        new("other", "Other")
    ];

    public static string SpeciesLabel(string? code) => Species.FirstOrDefault(s => s.Code == code)?.Label ?? code ?? "";

    public static string SpeciesIcon(string? code) => code switch
    {
        "goats" => "🐐",
        "cattle" => "🐄",
        "sheep" => "🐑",
        "pigs" => "🐖",
        "chickens" => "🐔",
        "ducks" => "🦆",
        "turkeys" => "🦃",
        "rabbits" => "🐇",
        _ => "🐾"
    };

    /// <summary>"K12,000" for kwacha (as people write it), "$450" for dollars.</summary>
    public static string Price(decimal? amount, string currency)
    {
        if (amount is not { } a)
            return "Price on request";
        var number = a.ToString(a == Math.Floor(a) ? "N0" : "N2", CultureInfo.InvariantCulture);
        return currency.ToUpperInvariant() switch
        {
            "ZMW" => "K" + number,
            "USD" => "$" + number,
            var other => $"{number} {other}"
        };
    }
}

public static class ListingStatuses
{
    /// <summary>For sale now.</summary>
    public const string Available = "available";

    /// <summary>Coming soon, e.g. "our upcoming Pekin parent flock" (§3.4 product descriptions).</summary>
    public const string Upcoming = "upcoming";

    public const string Sold = "sold";

    /// <summary>Taken down by the seller.</summary>
    public const string Withdrawn = "withdrawn";

    public static readonly IReadOnlyList<string> All = [Available, Upcoming, Sold, Withdrawn];

    public static string Label(string status) => status switch
    {
        Available => "For sale",
        Upcoming => "Coming soon",
        Sold => "Sold",
        Withdrawn => "Withdrawn",
        _ => status
    };

    public static bool IsOpen(string status) => status is Available or Upcoming;
}

public static class OfferKinds
{
    public const string Cash = "cash";

    /// <summary>A trade instead of money, e.g. "6 better crossbreed females" (§3.4 barter and exchange).</summary>
    public const string Barter = "barter";
}

public static class OfferStatuses
{
    public const string Pending = "pending";
    public const string Accepted = "accepted";
    public const string Declined = "declined";
    public const string Withdrawn = "withdrawn";

    public static string Label(string status) => status switch
    {
        Pending => "Waiting for the seller",
        Accepted => "Accepted",
        Declined => "Declined",
        Withdrawn => "Withdrawn",
        _ => status
    };
}

public static class MediaTypes
{
    public const string Image = "image";
    public const string Video = "video";
}

/// <summary>
/// A photo or video in a chat. Uploaded first (it comes back with its addresses), then sent in a
/// message by <see cref="Id"/>; the server fills in the rest.
/// </summary>
/// <param name="Url">The full photo, or the video, relative to the site.</param>
/// <param name="ThumbUrl">A small photo for galleries and cards; null for a video.</param>
public sealed record MediaItem(Guid Id, string Type, string Url, string? ThumbUrl, int? Width, int? Height, long Size);

/// <summary>Photos and/or videos, grouped as one gallery (§3.3), with an optional caption that may carry hashtags.</summary>
public sealed record MediaPayload(IReadOnlyList<MediaItem> Items, string? Caption = null, bool Forwarded = false);

/// <summary>
/// A listing as a chat message (§3.4). To post a new one, send everything but
/// <see cref="ListingId"/> (empty) and <see cref="Media"/> as uploaded items; to forward one, send
/// just its <see cref="ListingId"/> with <see cref="Forwarded"/>. The server fills in the rest.
/// </summary>
/// <param name="Age">As people say it: "5 years old", "8 weeks", "point of lay".</param>
/// <param name="BarterTerms">What the seller would take in exchange, e.g. "6 crossbreed females".</param>
public sealed record MarketListingPayload(
    Guid ListingId,
    Guid SellerId,
    string Title,
    string Species,
    string? Breed,
    int Quantity,
    decimal? Price,
    string Currency,
    string? Location,
    string? Age,
    string? Description,
    bool OpenToBarter,
    string? BarterTerms,
    string Status,
    IReadOnlyList<MediaItem> Media,
    IReadOnlyList<string> Tags,
    bool Forwarded = false);

/// <summary>A listing's status, kept current on every message that shows it.</summary>
public sealed record ListingState(string Status);

/// <summary>
/// An offer on a listing, posted in the chat the listing is in: money, or a trade (§3.4). The seller
/// accepts or declines it; the buyer can withdraw it while it's waiting.
/// </summary>
public sealed record MarketOfferPayload(
    Guid OfferId,
    Guid ListingId,
    string ListingTitle,
    Guid SellerId,
    string Kind,
    decimal? Amount,
    string? Currency,
    string? Text);

/// <summary>Where an offer stands, kept current on its message.</summary>
public sealed record OfferState(string Status);

/// <summary>Forwards a message (text, media or a listing) to other chats the person is in.</summary>
public sealed record ForwardRequest(IReadOnlyList<Guid> ConversationIds);

/// <summary>A listing in the Market app.</summary>
/// <param name="Chats">The chats it's posted in that the viewer is in, newest first: where to see it and make an offer.</param>
/// <param name="OfferCount">Offers still waiting (only for the seller; zero for others).</param>
public sealed record ListingDto(
    Guid Id,
    UserDto Seller,
    string Title,
    string Species,
    string? Breed,
    int Quantity,
    decimal? Price,
    string Currency,
    string? Location,
    string? Age,
    string? Description,
    bool OpenToBarter,
    string? BarterTerms,
    string Status,
    IReadOnlyList<MediaItem> Media,
    IReadOnlyList<string> Tags,
    IReadOnlyList<ListingChatDto> Chats,
    bool IsMine,
    int OfferCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <param name="MessageId">The listing's message in that chat, to reply to it there.</param>
public sealed record ListingChatDto(Guid ConversationId, string Title, Guid MessageId);

public sealed record ListingPageDto(IReadOnlyList<ListingDto> Listings, DateTimeOffset? Before);

/// <summary>An offer, as its listing's seller or its buyer sees it in the Market app.</summary>
public sealed record OfferDto(
    Guid Id,
    Guid ListingId,
    string ListingTitle,
    UserDto Buyer,
    UserDto Seller,
    string Kind,
    decimal? Amount,
    string? Currency,
    string? Text,
    string Status,
    Guid ConversationId,
    DateTimeOffset CreatedAt);

public sealed record SetListingStatusRequest(string Status);

/// <summary>A group chat someone can post a listing in.</summary>
public sealed record MarketChatDto(Guid Id, string Title, int MemberCount);

/// <summary>The Market app's own page: what you're selling and the offers you've made.</summary>
public sealed record MyMarketDto(IReadOnlyList<ListingDto> Selling, IReadOnlyList<OfferDto> OffersMade, IReadOnlyList<OfferDto> OffersReceived);

/// <summary>A popular hashtag across the listings someone can see, e.g. "purepekinpoultry".</summary>
public sealed record TagCountDto(string Tag, int Count);

/// <summary>Hashtags in text: "#PurePekinPoultry" (§3.2). Stored lower case; shown as written.</summary>
public static partial class Hashtags
{
    [GeneratedRegex(@"(?<![\p{L}\p{N}_#])#([\p{L}\p{N}_]{2,40})")]
    private static partial Regex Pattern();

    /// <summary>The distinct tags in the texts, lower case, in the order they first appear.</summary>
    public static IReadOnlyList<string> Find(params string?[] texts) =>
        texts.Where(t => !string.IsNullOrEmpty(t))
            .SelectMany(t => Pattern().Matches(t!).Select(m => m.Groups[1].Value.ToLowerInvariant()))
            .Distinct()
            .Take(20)
            .ToList();

    /// <summary>The text cut into plain runs and hashtags, for showing tags as links.</summary>
    public static IReadOnlyList<(string Text, string? Tag)> Split(string? text)
    {
        var parts = new List<(string, string?)>();
        if (string.IsNullOrEmpty(text))
            return parts;
        var at = 0;
        foreach (Match m in Pattern().Matches(text))
        {
            if (m.Index > at)
                parts.Add((text[at..m.Index], null));
            parts.Add((m.Value, m.Groups[1].Value.ToLowerInvariant()));
            at = m.Index + m.Length;
        }
        if (at < text.Length)
            parts.Add((text[at..], null));
        return parts;
    }

    /// <summary>"purepekinpoultry" (or "#PurePekinPoultry") as stored.</summary>
    public static string? Normalize(string? tag)
    {
        var t = tag?.Trim().TrimStart('#').ToLowerInvariant();
        return string.IsNullOrEmpty(t) ? null : t;
    }
}
