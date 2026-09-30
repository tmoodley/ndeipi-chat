using System.Collections.Specialized;
using NdeipiChat.App.Extensions;
using NdeipiChat.Client;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;

namespace NdeipiChat.App.Pages;

/// <summary>
/// Hosts a runtime-loaded sub-app in a WebView. It stays on the Ndeipi site: the web shell's
/// "close" (and its home page) come back to the launcher, and links elsewhere open in the browser.
/// </summary>
public partial class WebSubAppPage : ViewModelPage
{
    /// <summary>Tells the web shell it's in the app, so it accepts the sign-in handoff and hides its navigation.</summary>
    const string InAppUserAgent = "Mozilla/5.0 (Mobile; Ndeipi) " + MobileAuthContract.InAppAgentToken;

    readonly WebSubAppViewModel _viewModel;

    public WebSubAppPage(WebSubAppViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        WebContent.UserAgent = InAppUserAgent;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WebSubAppViewModel.Url) && _viewModel.Url is { } url)
                WebContent.Source = new UrlWebViewSource { Url = url.ToString() };
        };
    }

    void OnNavigating(object? sender, WebNavigatingEventArgs e)
    {
        // The path only: a handoff's query and fragment carry its one-time code and verifier.
        NdeipiChat.App.Platform.AppLog.Info($"web navigating {e.Url.Split('?', '#')[0]}");
        if (!Uri.TryCreate(e.Url, UriKind.Absolute, out var target) || _viewModel.Site is not { } site)
            return;
        // The WebView's own pages (it starts on about:blank) stay in it. They aren't somewhere else:
        // sending about:blank to Android made it ask which browser should open a blank page.
        if (target.Scheme is "about" or "data" or "blob" or "javascript")
            return;
        if (target.Scheme is not ("http" or "https"))
        {
            // mailto:, tel: and the like: whichever app handles them.
            e.Cancel = true;
            _ = Launcher.Default.TryOpenAsync(target);
            return;
        }
        if (!string.Equals(target.GetLeftPart(UriPartial.Authority), site.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            // Another website: the system browser, not this WebView.
            e.Cancel = true;
            _ = Browser.Default.OpenAsync(target, BrowserLaunchMode.SystemPreferred);
            return;
        }
        var path = target.AbsolutePath.Trim('/');
        if (path is "" or MobileAuthContract.EmbedClosePath)
        {
            e.Cancel = true;
            _ = CloseAsync();
        }
        else if (path == MobileAuthContract.EmbedSignInPath)
        {
            // The web shell's sign-in ran out: a fresh handoff, rather than Clerk's page in here.
            e.Cancel = true;
            _ = _viewModel.SignInAgainAsync();
        }
    }

    void OnNavigated(object? sender, WebNavigatedEventArgs e)
    {
        if (e.Result != WebNavigationResult.Success && _viewModel.ErrorMessage is null)
            _viewModel.ErrorMessage = $"Couldn't load {_viewModel.Title}. Check your connection and try again.";
    }

    async void OnClose(object? sender, EventArgs e) => await CloseAsync();

    static Task CloseAsync() => Shell.Current.GoToAsync("..");
}

public partial class LauncherPage : ViewModelPage
{
    readonly LauncherViewModel _viewModel;

    public LauncherPage(LauncherViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Loads once per launch; pull down to pick up changes to the manifest.</summary>
    protected override Task OnAppearedAsync() =>
        _viewModel.IsLoaded ? Task.CompletedTask : _viewModel.LoadCommand.ExecuteAsync(null);
}

public partial class HomePage : ViewModelPage
{
    readonly HomeViewModel _viewModel;

    public HomePage(HomeViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>
    /// Apps once per launch (they decide the tabs); chats and Shamwaris each time, since Home
    /// shows the latest of both. Each reports its own errors.
    /// </summary>
    protected override Task OnAppearedAsync() => Task.WhenAll(
        _viewModel.LoadCommand.ExecuteAsync(null),
        _viewModel.Chats.RefreshCommand.ExecuteAsync(null),
        _viewModel.Contacts.LoadCommand.ExecuteAsync(null));

    async void OnMe(object? sender, TappedEventArgs e) => await Shell.Current.GoToAsync("//main/me");

    async void OnSendMoney(object? sender, TappedEventArgs e) => await PickAsync("Transfer money to", HomeAction.SendMoney);

    async void OnSendTokens(object? sender, TappedEventArgs e) => await PickAsync("Send tokens to", HomeAction.SendTokens);

    /// <summary>Who to? Then straight onto the money or token form in your chat with them.</summary>
    async Task PickAsync(string title, HomeAction action)
    {
        var people = _viewModel.Everyone;
        if (people.Count == 0)
        {
            if (await DisplayAlertAsync(title.Split(' ')[0] + " who?", "Add a Shamwari first, then send them money or tokens from here.", "Find Shamwaris", "Cancel"))
                await _viewModel.OpenShamwarisCommand.ExecuteAsync(null);
            return;
        }
        var names = people.Select(p => p.Name).ToArray();
        var picked = await DisplayActionSheetAsync(title, "Cancel", null, names);
        if (people.FirstOrDefault(p => p.Name == picked) is { } person)
            await _viewModel.OpenAsync(person, action);
    }
}

public partial class FeedPage : ViewModelPage
{
    readonly FeedViewModel _viewModel;

    public FeedPage(FeedViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Loads once; pull down to refresh. A post published here is added without a reload.</summary>
    protected override Task OnAppearedAsync() =>
        _viewModel.Posts.Count == 0 ? _viewModel.RefreshCommand.ExecuteAsync(null) : Task.CompletedTask;

    async void OnDelete(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not PostItemViewModel post)
            return;
        // Undoing a plain repost needs no warning; deleting a post can't be taken back.
        if (post.IsPlainRepost || await DisplayAlertAsync("Delete post?", "This can't be undone.", "Delete", "Cancel"))
            await _viewModel.DeleteCommand.ExecuteAsync(post);
    }

    /// <summary>As LinkedIn: repost straight away, or with your thoughts; or take your repost back.</summary>
    async void OnRepost(object? sender, EventArgs e)
    {
        if ((sender as Button)?.CommandParameter is not PostItemViewModel post)
            return;
        const string Now = "Repost", WithThoughts = "Repost with your thoughts", Undo = "Undo repost";
        var choice = await DisplayActionSheetAsync("Repost", "Cancel", null, post.RepostedByMe ? [Undo, WithThoughts] : [Now, WithThoughts]);
        switch (choice)
        {
            case Now or Undo:
                await _viewModel.RepostCommand.ExecuteAsync(post);
                break;
            case WithThoughts:
                var thoughts = await DisplayPromptAsync("Repost with your thoughts", $"Sharing {post.AuthorName}'s post", "Repost", "Cancel",
                    "What do you think of it?", maxLength: SocialContract.MaxCaptionLength);
                if (thoughts is not null)
                    await _viewModel.RepostWithThoughtsAsync(post, thoughts);
                break;
        }
    }
}

public partial class ComposePostPage : ViewModelPage
{
    const int MaxEdge = 2048;

    readonly ComposePostViewModel _viewModel;

    public ComposePostPage(ComposePostViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Phone photos are shrunk as they're picked: less to upload, and well under the size limit.</summary>
    static MediaPickerOptions Options(int limit) => new()
    {
        SelectionLimit = limit,
        MaximumWidth = MaxEdge,
        MaximumHeight = MaxEdge,
        CompressionQuality = 90
    };

    async void OnTakePhoto(object? sender, EventArgs e) =>
        await AddAsync([await MediaPicker.Default.CapturePhotoAsync(Options(1))]);

    async void OnChoosePhotos(object? sender, EventArgs e) =>
        await AddAsync(await MediaPicker.Default.PickPhotosAsync(Options(SocialContract.MaxPhotos - _viewModel.Photos.Count)) ?? []);

    async Task AddAsync(IEnumerable<FileResult?> files)
    {
        foreach (var file in files)
        {
            if (file is null)
                continue;
            await using var stream = await file.OpenReadAsync();
            using var photo = new MemoryStream();
            await stream.CopyToAsync(photo);
            if (!_viewModel.AddPhoto(photo.ToArray()))
                break;
        }
    }
}

public partial class SignInPage : ViewModelPage
{
    public SignInPage(SignInViewModel viewModel) : base(viewModel) => InitializeComponent();
}

public partial class ChatsPage : ViewModelPage
{
    readonly ChatsViewModel _viewModel;

    public ChatsPage(ChatsViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override Task OnAppearedAsync() => _viewModel.RefreshCommand.ExecuteAsync(null);

    /// <summary>A new chat starts from Shamwaris, which opens over Home.</summary>
    async void OnNewChat(object? sender, EventArgs e) => await Shell.Current.GoToAsync($"//main/home/{AppShell.PageRoute(BuiltInApps.Shamwaris)}");
}

public partial class ChatPage : ViewModelPage
{
    readonly ChatViewModel _viewModel;

    public ChatPage(ChatViewModel viewModel, MessageTemplateSelector templates) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        MessageList.ItemTemplate = templates;
        _viewModel.Messages.CollectionChanged += OnMessagesChanged;
    }

    /// <summary>Opening a chat lands on the newest message; KeepLastItemInView handles the rest.</summary>
    void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset || (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && _viewModel.Messages.Count == e.NewItems?.Count))
            Dispatcher.Dispatch(() =>
            {
                if (_viewModel.Messages.Count > 0)
                    MessageList.ScrollTo(_viewModel.Messages.Count - 1, position: ScrollToPosition.End, animate: false);
            });
    }

    protected override void OnNavigatedFrom(NavigatedFromEventArgs args)
    {
        if (args.NavigationType is NavigationType.Pop or NavigationType.PopToRoot or NavigationType.Remove)
            _viewModel.Messages.CollectionChanged -= OnMessagesChanged;
        base.OnNavigatedFrom(args);
    }
}

public partial class ContactsPage : ViewModelPage
{
    readonly ContactsViewModel _viewModel;

    public ContactsPage(ContactsViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    protected override Task OnAppearedAsync() => _viewModel.LoadCommand.ExecuteAsync(null);
}

public partial class MePage : ViewModelPage
{
    readonly MeViewModel _viewModel;
    readonly INavigator _navigator;

    public MePage(MeViewModel viewModel, INavigator navigator) : base(viewModel)
    {
        InitializeComponent();
        (_viewModel, _navigator) = (viewModel, navigator);
        DarkMode.IsToggled = Application.Current?.RequestedTheme == AppTheme.Dark;
    }

    protected override Task OnAppearedAsync() => _viewModel.LoadCommand.ExecuteAsync(null);

    async void OnShamwaris(object? sender, TappedEventArgs e) => await _navigator.GoToAsync(Routes.SubApp(BuiltInApps.Shamwaris));

    async void OnAllApps(object? sender, TappedEventArgs e) => await _navigator.GoToAsync(Routes.Launcher);

    void OnDarkModeToggled(object? sender, ToggledEventArgs e) => App.SetTheme(e.Value ? AppTheme.Dark : AppTheme.Light);
}

public partial class WalletPage : ViewModelPage
{
    readonly WalletViewModel _viewModel;

    public WalletPage(WalletViewModel viewModel) : base(viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
    }

    /// <summary>Also runs when the user comes back from Bridge's KYC pages in the browser.</summary>
    protected override Task OnAppearedAsync() => _viewModel.LoadCommand.ExecuteAsync(null);

    async void OnCopyAddress(object? sender, EventArgs e)
    {
        if (_viewModel.Status?.WalletAddress is not { } address)
            return;
        await Clipboard.Default.SetTextAsync(address);
        if (sender is Button button)
            button.Text = "Copied ✓";
    }
}

public partial class AssetTransferPage : ViewModelPage
{
    public AssetTransferPage(AssetTransferViewModel viewModel) : base(viewModel) => InitializeComponent();
}

public partial class BankTransferPage : ViewModelPage
{
    public BankTransferPage(BankTransferViewModel viewModel) : base(viewModel) => InitializeComponent();
}
