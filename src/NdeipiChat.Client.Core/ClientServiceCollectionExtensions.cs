using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NdeipiChat.Client.Auth;
using NdeipiChat.Client.Extensions;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Client.ViewModels;

namespace NdeipiChat.Client;

public static class ClientServiceCollectionExtensions
{
    /// <summary>
    /// Everything but the platform: the app supplies <see cref="ITokenStore"/>,
    /// <see cref="IBrowserAuthenticator"/>, <see cref="INavigator"/>, <see cref="IDialogs"/> and
    /// <see cref="IUiDispatcher"/>.
    /// </summary>
    public static IServiceCollection AddNdeipiChatClient(this IServiceCollection services, ClientOptions options)
    {
        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);

        services.AddHttpClient(AuthService.HttpClientName, c => c.BaseAddress = options.ApiBaseUrl);
        services.AddHttpClient(ChatApi.HttpClientName, c => c.BaseAddress = options.ApiBaseUrl)
            .AddHttpMessageHandler<AuthHeaderHandler>();
        services.AddTransient<AuthHeaderHandler>();

        services.AddSingleton(sp => new AuthService(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(AuthService.HttpClientName),
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<IBrowserAuthenticator>(),
            options,
            sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new ChatApi(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ChatApi.HttpClientName)));
        services.AddSingleton(sp => new ChatConnection(options, sp.GetRequiredService<AuthService>()));
        services.AddSingleton<ChatSession>();
        services.AddSingleton<ChatExtensions>();
        services.AddSingleton<AppCoordinator>();

        // Livestock registry. The app supplies ILocationProvider, ISettingsStore and IOperatorKeyStore.
        services.AddSingleton(sp => new LivestockApi(sp.GetRequiredService<IHttpClientFactory>().CreateClient(ChatApi.HttpClientName)));
        services.AddSingleton<ILivestockCaptureQueue, LivestockCaptureQueue>();
        services.AddSingleton<IP256Signer, DotNetP256Signer>();
        services.AddSingleton<OperatorSigner>();
        services.AddSingleton<LivestockSync>();

        services
            .AddMessageRenderer<TextMessageRenderer>()
            .AddMessageRenderer<AssetTransferRenderer>()
            .AddMessageRenderer<BankTransferRenderer>()
            .AddComposerAction<SendTokenAction>()
            .AddComposerAction<SendMoneyAction>()
            .AddComposerAction<StartGigAction>();

        services.AddTransient<SignInViewModel>();
        services.AddSingleton<ChatsViewModel>();
        services.AddTransient<ChatViewModel>();
        services.AddSingleton<ContactsViewModel>();
        services.AddSingleton<FeedViewModel>();
        services.AddSingleton<LauncherViewModel>();
        services.AddSingleton<HomeViewModel>();
        services.AddTransient<WebSubAppViewModel>();

        // Sub-apps must be signed by a publisher key compiled into the shell. A shell may register its
        // own SubAppTrust first, e.g. to allow unsigned bundles in local development.
        services.TryAddSingleton(SubAppTrust.Of(requireSignature: true, PublisherKeys.All));
        services.AddSingleton<SubAppVerifier>();
        services.AddTransient<ComposePostViewModel>();
        services.AddTransient<AssetTransferViewModel>();
        services.AddTransient<BankTransferViewModel>();
        services.AddTransient<WalletViewModel>();
        services.AddSingleton<MeViewModel>();
        services.AddSingleton<HerdViewModel>();
        services.AddTransient<RegisterCowViewModel>();
        services.AddTransient<CowDetailViewModel>();
        return services;
    }

    /// <summary>Adds the app half of a chat extension: how messages of one kind are shown.</summary>
    public static IServiceCollection AddMessageRenderer<T>(this IServiceCollection services) where T : class, IMessageRenderer =>
        services.AddSingleton<IMessageRenderer, T>();

    /// <summary>Adds a tile to the chat's "+" panel.</summary>
    public static IServiceCollection AddComposerAction<T>(this IServiceCollection services) where T : class, IComposerAction =>
        services.AddSingleton<IComposerAction, T>();
}
