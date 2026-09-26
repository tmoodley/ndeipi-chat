using NdeipiChat.App.Pages;
using NdeipiChat.Client;

namespace NdeipiChat.App;

public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        Routing.RegisterRoute(Routes.Chat, typeof(ChatPage));
        Routing.RegisterRoute(Routes.AssetTransfer, typeof(AssetTransferPage));
        Routing.RegisterRoute(Routes.BankTransfer, typeof(BankTransferPage));
        Routing.RegisterRoute(Routes.Wallet, typeof(WalletPage));
        Routing.RegisterRoute(Routes.RegisterCow, typeof(RegisterCowPage));
        Routing.RegisterRoute(Routes.Cow, typeof(CowDetailPage));
        Routing.RegisterRoute(Routes.ComposePost, typeof(ComposePostPage));
    }
}
