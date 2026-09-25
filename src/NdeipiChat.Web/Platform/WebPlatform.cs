using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using NdeipiChat.Client;
using NdeipiChat.Client.Auth;
using NdeipiChat.Contracts;

namespace NdeipiChat.Web.Platform;

/// <summary>localStorage and sessionStorage, through ndeipi.js.</summary>
public sealed class BrowserStorage(IJSRuntime js)
{
    public ValueTask<string?> GetAsync(string area, string key) => js.InvokeAsync<string?>("ndeipi.storage.get", area, key);
    public ValueTask SetAsync(string area, string key, string value) => js.InvokeVoidAsync("ndeipi.storage.set", area, key, value);
    public ValueTask RemoveAsync(string area, string key) => js.InvokeVoidAsync("ndeipi.storage.remove", area, key);
}

/// <summary>Keeps the sign-in in localStorage, so it survives reloads and new tabs.</summary>
public sealed class BrowserTokenStore(BrowserStorage storage) : ITokenStore
{
    const string Key = "ndeipi.tokens";

    public async Task<TokenResponse?> LoadAsync()
    {
        var json = await storage.GetAsync("localStorage", Key);
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<TokenResponse>(json, ContractJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(TokenResponse tokens) =>
        await storage.SetAsync("localStorage", Key, JsonSerializer.Serialize(tokens, ContractJson.Options));

    public async Task ClearAsync() => await storage.RemoveAsync("localStorage", Key);
}

/// <summary>
/// The web app signs in by leaving the page for the API's sign-in page and coming back to
/// <c>/signin/callback</c> (see <see cref="WebSignIn"/>). A popup would keep the app's own flow,
/// but Google and other providers cut a popup off from its opener, so the result never arrives.
/// </summary>
public sealed class RedirectOnlyAuthenticator : IBrowserAuthenticator
{
    public Task<IReadOnlyDictionary<string, string>> AuthenticateAsync(Uri url, Uri callbackUri, CancellationToken ct) =>
        throw new NotSupportedException("The web app signs in with WebSignIn.");
}

/// <summary>Both halves of the redirect sign-in. The PKCE secrets wait in this tab's sessionStorage.</summary>
public sealed class WebSignIn(AuthService auth, BrowserStorage storage, NavigationManager navigation)
{
    const string Key = "ndeipi.pending-sign-in";

    public async Task StartAsync()
    {
        var (url, pending) = auth.StartSignIn();
        await storage.SetAsync("sessionStorage", Key, JsonSerializer.Serialize(pending));
        navigation.NavigateTo(url.ToString(), forceLoad: true);
    }

    public async Task CompleteAsync(IReadOnlyDictionary<string, string> query)
    {
        var json = await storage.GetAsync("sessionStorage", Key);
        await storage.RemoveAsync("sessionStorage", Key);
        PendingSignIn? pending = null;
        try
        {
            pending = json is null ? null : JsonSerializer.Deserialize<PendingSignIn>(json);
        }
        catch (JsonException)
        {
        }

        // Nothing waiting: the callback was opened in another tab, or reloaded after it ran.
        if (pending is null)
            throw new AuthException("Sign-in was interrupted. Please try again.");
        await auth.CompleteSignInAsync(pending, query);
    }
}

/// <summary>Maps the app's routes onto URLs.</summary>
public sealed class WebNavigator(NavigationManager navigation, IJSRuntime js) : INavigator
{
    public const string SignInPath = "signin";
    public const string CallbackPath = "signin/callback";

    public Task GoToAsync(string route, IDictionary<string, object>? parameters = null)
    {
        Guid Conversation() => parameters?[Routes.ConversationIdParameter] is Guid id ? id : Guid.Empty;
        navigation.NavigateTo(route switch
        {
            Routes.Chat => $"chat/{Conversation()}",
            Routes.BankTransfer => $"chat/{Conversation()}/send-money",
            Routes.AssetTransfer => $"chat/{Conversation()}/transfer",
            Routes.Wallet => "wallet",
            _ => "chats"
        });
        return Task.CompletedTask;
    }

    public async Task GoBackAsync() => await js.InvokeVoidAsync("ndeipi.back");

    /// <summary>Only from the landing and sign-in pages; a signed-in deep link stays where it is.</summary>
    public Task ShowMainAsync()
    {
        if (CurrentPath is "" or SignInPath or CallbackPath)
            navigation.NavigateTo("chats", replace: true);
        return Task.CompletedTask;
    }

    /// <summary>Not while a sign-in is coming back through the callback page.</summary>
    public Task ShowSignInAsync()
    {
        if (CurrentPath is not (SignInPath or CallbackPath))
            navigation.NavigateTo(SignInPath, replace: true);
        return Task.CompletedTask;
    }

    string CurrentPath => navigation.ToBaseRelativePath(navigation.Uri).Split('?', '#')[0].TrimEnd('/');
}

public sealed class WebDialogs(IJSRuntime js) : IDialogs
{
    public async Task AlertAsync(string title, string message) => await js.InvokeVoidAsync("ndeipi.alert", title, message);

    public async Task OpenBrowserAsync(Uri url) => await js.InvokeVoidAsync("ndeipi.open", url.ToString());
}

/// <summary>Livestock registration isn't on the web, so there's no position to give.</summary>
public sealed class NoLocationProvider : ILocationProvider
{
    public Task<GpsTelemetry?> GetLocationAsync(CancellationToken ct) => Task.FromResult<GpsTelemetry?>(null);
}

/// <summary>
/// The browser has one thread, so work runs straight away. <see cref="Changed"/> then tells the
/// pages on screen to re-render, since hub events change view models outside Blazor's own events.
/// </summary>
public sealed class WebDispatcher : IUiDispatcher
{
    public event Action? Changed;

    public void Post(Action action)
    {
        action();
        Changed?.Invoke();
    }
}
