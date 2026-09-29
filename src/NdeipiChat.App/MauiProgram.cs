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
    /// <summary>Text fields sit in rounded boxes (the Field style), as on the web: no platform underline.</summary>
    static void RemoveFieldUnderlines()
    {
#if ANDROID
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
            handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent));
        Microsoft.Maui.Handlers.EditorHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
            handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent));
        Microsoft.Maui.Handlers.PickerHandler.Mapper.AppendToMapping("NoUnderline", (handler, _) =>
            handler.PlatformView.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent));
#endif
    }

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.UseMauiCommunityToolkitCamera();
        RemoveFieldUnderlines();
#if DEBUG && ANDROID
        // chrome://inspect can then show a sub-app's WebView: its console and where it navigates.
        Android.Webkit.WebView.SetWebContentsDebuggingEnabled(true);
#endif

#if DEBUG
        // A debug build may run against a local API, whose sub-apps nobody has signed. Release builds
        // take the client's default: signatures required, by the keys compiled in (PublisherKeys).
        builder.Services.AddSingleton(SubAppTrust.Of(requireSignature: false, PublisherKeys.All));
#endif
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
        builder.Services.AddTransient<LauncherPage>();
        builder.Services.AddTransient<HomePage>();
        builder.Services.AddTransient<WebSubAppPage>();
        builder.Services.AddTransient<FeedPage>();
        builder.Services.AddTransient<ComposePostPage>();
        builder.Services.AddSingleton<AppShell>();

#if DEBUG
        builder.Logging.AddDebug();
#endif
        return builder.Build();
    }
}
