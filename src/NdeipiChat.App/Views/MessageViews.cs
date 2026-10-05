using NdeipiChat.Client;
using NdeipiChat.Client.Extensions;
using NdeipiChat.Client.Realtime;

namespace NdeipiChat.App.Views;

public partial class TextMessageView : MessageRow
{
    public TextMessageView() => InitializeComponent();
}

public partial class AssetTransferMessageView : MessageRow
{
    public AssetTransferMessageView() => InitializeComponent();
}

public partial class BankTransferMessageView : MessageRow
{
    public BankTransferMessageView() => InitializeComponent();
}

public partial class UnsupportedMessageView : MessageRow
{
    public UnsupportedMessageView() => InitializeComponent();
}

public partial class PosReceiptMessageView : MessageRow
{
    public PosReceiptMessageView() => InitializeComponent();
}

/// <summary>Shared by the market views: the app's services, from the running app.</summary>
static class AppServices
{
    public static T Get<T>() where T : notnull =>
        (IPlatformApplication.Current?.Services ?? throw new InvalidOperationException("The app isn't running.")).GetRequiredService<T>();
}

public partial class MediaMessageView : MessageRow
{
    public MediaMessageView() => InitializeComponent();

    /// <summary>One item fills the bubble; more go two to a row, square.</summary>
    void OnTileLoaded(object? sender, EventArgs e)
    {
        if (sender is not Grid tile || BindingContext is not MediaMessageViewModel message)
            return;
        (tile.WidthRequest, tile.HeightRequest) = message.Items.Count == 1 ? (258d, 194d) : (127d, 127d);
    }

    /// <summary>Full size, or playing, in the phone's browser.</summary>
    async void OnTileTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as BindableObject)?.BindingContext is MediaItemViewModel item)
            await Browser.Default.OpenAsync(item.Url, BrowserLaunchMode.SystemPreferred);
    }
}

public partial class ListingMessageView : MessageRow
{
    public ListingMessageView() => InitializeComponent();

    async void OnOpen(object? sender, EventArgs e)
    {
        if (BindingContext is ListingMessageViewModel { Delivery: DeliveryStatus.Sent } listing)
            await MarketNavigation.OpenListingAsync(AppServices.Get<INavigator>(), listing.ListingId);
    }

    /// <summary>The Market's offer form, posting the offer back into this chat.</summary>
    async void OnOffer(object? sender, EventArgs e)
    {
        if (BindingContext is ListingMessageViewModel listing)
            await MarketNavigation.OpenAsync(AppServices.Get<INavigator>(), $"listing={listing.ListingId:N}&chat={listing.Message.ConversationId:N}&offer=1");
    }
}

public partial class OfferMessageView : MessageRow
{
    public OfferMessageView() => InitializeComponent();

    void OnAccept(object? sender, EventArgs e) => _ = AnswerAsync("accept");
    void OnDecline(object? sender, EventArgs e) => _ = AnswerAsync("decline");
    void OnWithdraw(object? sender, EventArgs e) => _ = AnswerAsync("withdraw");

    async Task AnswerAsync(string answer)
    {
        if (BindingContext is not OfferMessageViewModel offer)
            return;
        try
        {
            offer.Status = (await AppServices.Get<ChatApi>().AnswerOfferAsync(offer.OfferId, answer)).Status;
        }
        catch (ApiException ex)
        {
            await Shell.Current.DisplayAlertAsync("Couldn't answer the offer", ex.Message, "OK");
        }
    }
}
