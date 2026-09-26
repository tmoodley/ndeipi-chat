using CommunityToolkit.Maui;
using Microsoft.Extensions.Logging;
using NdeipiChat.Client.Livestock;
using NdeipiChat.App.Extensions;
using NdeipiChat.App.Pages;
using NdeipiChat.App.Platform;
using NdeipiChat.App.Views;
using NdeipiChat.Client;
using NdeipiChat.Client.Extensions;

namespace NdeipiChat.App;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.UseMauiCommunityToolkitCamera();

        builder.Services.AddNdeipiChatClient(new ClientOptions
        {
            ApiBaseUrl = new Uri(AppConfig.ApiBaseUrl),
            RedirectUri = AppConfig.RedirectUri,
            DataDirectory = FileSystem.Current.AppDataDirectory,
            DeviceName = DeviceInfo.Current.Name
        });

        builder.Services.AddSingleton<ITokenStore, SecureTokenStore>();
        builder.Services.AddSingleton<IBrowserAuthenticator, MauiBrowserAuthenticator>();
        builder.Services.AddSingleton<INavigator, ShellNavigator>();
        builder.Services.AddSingleton<IDialogs, MauiDialogs>();
        builder.Services.AddSingleton<IUiDispatcher, MauiDispatcher>();
        builder.Services.AddSingleton<ILocationProvider, MauiLocationProvider>();
        builder.Services.AddSingleton<ISettingsStore, PreferencesSettingsStore>();
        builder.Services.AddSingleton<IOperatorKeyStore, SecureOperatorKeyStore>();

        // Chat extensions, app side: which view draws each message view model. A new message kind
        // adds a renderer (Client.Core), a template here, and a handler on the API.
        builder.Services
            .AddMessageTemplate<TextMessageViewModel, TextMessageView>()
            .AddMessageTemplate<AssetTransferMessageViewModel, AssetTransferMessageView>()
            .AddMessageTemplate<BankTransferMessageViewModel, BankTransferMessageView>()
            .AddMessageTemplate<UnsupportedMessageViewModel, UnsupportedMessageView>();
        builder.Services.AddSingleton<MessageTemplateSelector>();

        builder.Services.AddTransient<SignInPage>();
        builder.Services.AddTransient<ChatsPage>();
        builder.Services.AddTransient<ContactsPage>();
        builder.Services.AddTransient<MePage>();
        builder.Services.AddTransient<ChatPage>();
        builder.Services.AddTransient<AssetTransferPage>();
        builder.Services.AddTransient<BankTransferPage>();
        builder.Services.AddTransient<WalletPage>();
        builder.Services.AddTransient<HerdPage>();
        builder.Services.AddTransient<RegisterCowPage>();
        builder.Services.AddTransient<CowDetailPage>();
        builder.Services.AddTransient<FeedPage>();
        builder.Services.AddTransient<ComposePostPage>();
        builder.Services.AddSingleton<AppShell>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
