using NdeipiChat.App.Pages;
using NdeipiChat.Client;
using NdeipiChat.Contracts;

namespace NdeipiChat.App;

public partial class AppShell : Shell
{
    /// <summary>Every app, to open and pin: opened from Home and More.</summary>
    public const string AllAppsRoute = "all-apps";

    /// <summary>
    /// Each sub-app's page as a route of its own too, so one without a tab opens on top of Home
    /// instead ("subapp-herd"). Wallet always has its tab.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Type> SubAppPages = new Dictionary<string, Type>
    {
        [BuiltInApps.Chats] = typeof(ChatsPage),
        [BuiltInApps.Feed] = typeof(FeedPage),
        [BuiltInApps.Shamwaris] = typeof(ContactsPage),
        [BuiltInApps.Herd] = typeof(HerdPage),
        [BuiltInApps.Wallet] = typeof(WalletPage)
    };

    /// <summary>The apps with a place of their own in the dock, by id: their tab routes.</summary>
    static readonly IReadOnlyDictionary<string, string> TabRoutes = new Dictionary<string, string>
    {
        [BuiltInApps.Chats] = "chats",
        [BuiltInApps.Feed] = "feed",
        [BuiltInApps.Wallet] = "wallet"
    };

    public AppShell()
    {
        InitializeComponent();

        Routing.RegisterRoute(Routes.Chat, typeof(ChatPage));
        Routing.RegisterRoute(Routes.AssetTransfer, typeof(AssetTransferPage));
        Routing.RegisterRoute(Routes.BankTransfer, typeof(BankTransferPage));
        Routing.RegisterRoute(Routes.RegisterCow, typeof(RegisterCowPage));
        Routing.RegisterRoute(Routes.Cow, typeof(CowDetailPage));
        Routing.RegisterRoute(Routes.ComposePost, typeof(ComposePostPage));
        Routing.RegisterRoute(Routes.WebSubApp, typeof(WebSubAppPage));
        Routing.RegisterRoute(AllAppsRoute, typeof(LauncherPage));
        foreach (var (id, page) in SubAppPages.Where(p => !TabRoutes.ContainsKey(p.Key)))
            Routing.RegisterRoute(PageRoute(id), page);
    }

    public static string PageRoute(string appId) => "subapp-" + appId;

    /// <summary>The tab route of a sub-app with a place in the dock, or null to open it as a page.</summary>
    public static string? TabRouteFor(string appId) => TabRoutes.GetValueOrDefault(appId);
}
