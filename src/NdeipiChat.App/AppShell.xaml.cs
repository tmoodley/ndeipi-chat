using NdeipiChat.App.Pages;
using NdeipiChat.Client;
using NdeipiChat.Client.ViewModels;
using NdeipiChat.Contracts;

namespace NdeipiChat.App;

public partial class AppShell : Shell
{
    public const int MaxPinnedTabs = 3;

    /// <summary>
    /// Each sub-app's page as a route of its own too, so one without a tab opens on top of the
    /// launcher instead ("subapp-herd"). Wallet has no tab; it always opens this way.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> SubAppPages = new Dictionary<string, Type>
    {
        [BuiltInApps.Chats] = typeof(ChatsPage),
        [BuiltInApps.Feed] = typeof(FeedPage),
        [BuiltInApps.Shamwaris] = typeof(ContactsPage),
        [BuiltInApps.Herd] = typeof(HerdPage),
        [BuiltInApps.Wallet] = typeof(WalletPage)
    };

    readonly LauncherViewModel _launcher;
    readonly Dictionary<string, Tab> _tabs;

    public AppShell(LauncherViewModel launcher)
    {
        InitializeComponent();
        _launcher = launcher;
        _tabs = new()
        {
            [BuiltInApps.Chats] = ChatsTab,
            [BuiltInApps.Feed] = FeedTab,
            [BuiltInApps.Shamwaris] = ShamwarisTab,
            [BuiltInApps.Herd] = HerdTab
        };

        Routing.RegisterRoute(Routes.Chat, typeof(ChatPage));
        Routing.RegisterRoute(Routes.AssetTransfer, typeof(AssetTransferPage));
        Routing.RegisterRoute(Routes.BankTransfer, typeof(BankTransferPage));
        Routing.RegisterRoute(Routes.Wallet, typeof(WalletPage));
        Routing.RegisterRoute(Routes.RegisterCow, typeof(RegisterCowPage));
        Routing.RegisterRoute(Routes.Cow, typeof(CowDetailPage));
        Routing.RegisterRoute(Routes.ComposePost, typeof(ComposePostPage));
        Routing.RegisterRoute(Routes.WebSubApp, typeof(WebSubAppPage));
        foreach (var (id, page) in SubAppPages.Where(p => p.Key != BuiltInApps.Wallet))
            Routing.RegisterRoute(PageRoute(id), page);

        _launcher.ManifestChanged += () => MainThread.BeginInvokeOnMainThread(ShowPinnedTabs);
    }

    public static string PageRoute(string appId) => "subapp-" + appId;

    /// <summary>The tab route of a sub-app whose tab is showing, or null to open it as a page.</summary>
    public string? TabRouteFor(string appId) => _tabs.TryGetValue(appId, out var tab) && tab.IsVisible ? tab.Route : null;

    /// <summary>Tabs for the first three pinned apps the user may use; the manifest leaves out the rest.</summary>
    void ShowPinnedTabs()
    {
        var shown = _launcher.Pinned.Select(a => a.Id).Where(_tabs.ContainsKey).Take(MaxPinnedTabs).ToHashSet();
        foreach (var (id, tab) in _tabs)
            tab.IsVisible = shown.Contains(id);
    }
}
