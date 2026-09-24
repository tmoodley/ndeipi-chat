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
        MainThread.InvokeOnMainThreadAsync(() => parameters is null
            ? Shell.Current.GoToAsync(route)
            : Shell.Current.GoToAsync(route, parameters));

    public Task GoBackAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync(".."));

    public Task ShowMainAsync() => MainThread.InvokeOnMainThreadAsync(() => Shell.Current.GoToAsync("//main/chats"));

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
