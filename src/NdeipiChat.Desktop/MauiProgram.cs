using NdeipiChat.Client;
using NdeipiChat.Client.Auth;

namespace NdeipiChat.Desktop;

public static class MauiProgram
{
    /// <summary>The web app the desktop window shows; the API is on the same origin.</summary>
    public static readonly Uri Site = new("https://chat.ndeipi.com/");

    public static MauiApp CreateMauiApp()
    {
        // WebView2 otherwise keeps its profile beside Ndeipi.exe, which fails in a read-only folder
        // such as Program Files; keep it with the user's app data instead.
        if (OperatingSystem.IsWindows())
            Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ndeipi", "WebView2"));

        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        var options = new ClientOptions
        {
            ApiBaseUrl = Site,
            // Windows has no app-link scheme handoff for unpackaged apps, so the browser returns the
            // sign-in to a listener on this PC; the Mac app uses the phone app's scheme.
            RedirectUri = OperatingSystem.IsWindows() ? LoopbackAuthenticator.CallbackUrl : "ndeipichat://auth",
            DeviceName = OperatingSystem.IsWindows() ? "Windows desktop" : "Mac desktop"
        };
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<ITokenStore, DesktopTokenStore>();
        if (OperatingSystem.IsWindows())
            builder.Services.AddSingleton<IBrowserAuthenticator, LoopbackAuthenticator>();
        else
            builder.Services.AddSingleton<IBrowserAuthenticator, SystemBrowserAuthenticator>();
        builder.Services.AddSingleton(sp => new AuthService(
            new HttpClient { BaseAddress = Site, Timeout = TimeSpan.FromSeconds(30) },
            sp.GetRequiredService<ITokenStore>(),
            sp.GetRequiredService<IBrowserAuthenticator>(),
            options,
            TimeProvider.System));
        builder.Services.AddSingleton<ShellPage>();
        return builder.Build();
    }
}
