using NdeipiChat.Client;
using NdeipiChat.Contracts;

namespace NdeipiChat.App.Platform;

/// <summary>Keeps the sign-in in the Keychain (iOS) or Keystore-backed storage (Android).</summary>
public sealed class SecureTokenStore : ITokenStore
{
    const string Key = "ndeipi.chat.tokens";

    public async Task<TokenResponse?> LoadAsync()
    {
        try
        {
            var json = await SecureStorage.Default.GetAsync(Key);
            return json is null ? null : ContractJson.Read<TokenResponse>(json);
        }
        catch (Exception)
        {
            // Unreadable after a restore to another device or a key reset; start signed out.
            SecureStorage.Default.Remove(Key);
            return null;
        }
    }

    public Task SaveAsync(TokenResponse tokens) => SecureStorage.Default.SetAsync(Key, ContractJson.Write(tokens));

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(Key);
        return Task.CompletedTask;
    }
}

/// <summary>
/// ASWebAuthenticationSession on iOS, Custom Tabs on Android. Shares the browser's cookies, so a
/// user already signed in to Clerk goes straight through.
/// </summary>
public sealed class MauiBrowserAuthenticator : IBrowserAuthenticator
{
    public async Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct)
    {
        var result = await WebAuthenticator.Default.AuthenticateAsync(new WebAuthenticatorOptions
        {
            Url = url,
            CallbackUrl = callbackUri,
            PrefersEphemeralWebBrowserSession = false
        });
        return result.Properties;
    }
}

public sealed class ShellNavigator : INavigator
{
    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null) =>
        MainThread.InvokeOnMainThreadAsync(() =>
        {
            AppLog.Info($"nav go {route} from {Shell.Current.CurrentState.Location}");
            if (route == Routes.Launcher)
                return Shell.Current.GoToAsync($"//main/home/{AppShell.AllAppsRoute}");
            if (route == Routes.Wallet)
                return Shell.Current.GoToAsync("//main/wallet");
            // Ndeipi Pay is the web shell's pay page, signed in, in the WebView on top of this tab.
            if (route == Routes.ScanToPay)
                return Shell.Current.GoToAsync(Routes.WebSubApp, new Dictionary<string, object> { [Routes.WebPageParameter] = PosPay.PayPath });
            if (route == Routes.Trust)
                return Shell.Current.GoToAsync(Routes.WebSubApp, new Dictionary<string, object> { [Routes.WebPageParameter] = TrustContract.PagePath });
            if (route.StartsWith(Routes.SubAppPrefix, StringComparison.Ordinal))
                return OpenSubAppAsync(route[Routes.SubAppPrefix.Length..],
                    parameters is not null && parameters.TryGetValue(Routes.SubAppRouteParameter, out var appRoute) ? appRoute as string : null);
            return parameters is null ? Shell.Current.GoToAsync(route) : Shell.Current.GoToAsync(route, parameters);
        });

    /// <summary>
    /// A loaded sub-app (route apps/{id}) opens in the WebView page. A built-in one opens on its tab
    /// if it has a place in the dock, or else as a page on top of Home.
    /// </summary>
    static Task OpenSubAppAsync(string appId, string? route)
    {
        if (route?.StartsWith("apps/", StringComparison.Ordinal) == true)
            return Shell.Current.GoToAsync($"//main/home/{Routes.WebSubApp}",
                new Dictionary<string, object> { [Routes.SubAppIdParameter] = appId, [Routes.SubAppRouteParameter] = route! });
        if (AppShell.TabRouteFor(appId) is { } tab)
            return Shell.Current.GoToAsync($"//main/{tab}");
        if (AppShell.SubAppPages.ContainsKey(appId))
            return Shell.Current.GoToAsync($"//main/home/{AppShell.PageRoute(appId)}");
        // In the manifest, but not in this version of the app (sub-apps loaded at runtime come later).
        return Shell.Current.DisplayAlertAsync("Not available yet", "This app isn't in this version of Ndeipi. Update the app to use it.", "OK");
    }

    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));

    public Task ShowMainAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync("//main/home"));

    public Task ShowSignInAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync("//signin"));
}

public sealed class MauiDialogs : IDialogs
{
    public Task AlertAsync(string title, string message) =>
        MainThread.InvokeOnMainThreadAsync(() => Shell.Current.DisplayAlertAsync(title, message, "OK"));

    public Task OpenBrowserAsync(Uri url) => Browser.Default.OpenAsync(url, BrowserLaunchMode.SystemPreferred);
}

public sealed class MauiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        if (MainThread.IsMainThread)
            action();
        else
            MainThread.BeginInvokeOnMainThread(action);
    }
}
