using System.Buffers.Text;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Auth;

/// <summary>
/// Clerk sign-in for the app. Opens the API's sign-in page in the system browser (Clerk's own UI
/// runs there), receives a one-time code on the app's redirect URI and redeems it with the PKCE
/// verifier only this app instance knows. Access tokens are refreshed shortly before they expire.
/// </summary>
public sealed class AuthService(HttpClient http, ITokenStore store, IBrowserAuthenticator browser, ClientOptions options, TimeProvider clock)
{
    public const string HttpClientName = "ndeipi-auth";

    static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(20);

    readonly SemaphoreSlim _refreshLock = new(1, 1);
    TokenResponse? _tokens;
    bool _loaded;

    /// <summary>Raised when the sign-in ends underneath the app (revoked, expired, signed out elsewhere).</summary>
    public event Action? SignedOut;

    public async Task<bool> IsSignedInAsync() => await LoadAsync() is not null;

    public async Task<Guid?> GetUserIdAsync() => (await LoadAsync())?.UserId;

    public async Task SignInAsync(CancellationToken ct = default)
    {
        var verifier = Pkce.NewSecret();
        var state = Pkce.NewSecret();
        var url = new Uri(options.ApiBaseUrl, MobileAuthContract.SignInPath.TrimStart('/')
            + $"?redirect_uri={Uri.EscapeDataString(options.RedirectUri)}"
            + $"&code_challenge={Pkce.Challenge(verifier)}&code_challenge_method=S256"
            + $"&state={state}");

        var result = await browser.AuthenticateAsync(url, new Uri(options.RedirectUri), ct);
        if (!result.TryGetValue("state", out var returnedState) || returnedState != state)
            throw new AuthException("Sign-in was interrupted. Please try again.");
        if (!result.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
            throw new AuthException("Sign-in didn't complete. Please try again.");

        using var response = await http.PostAsJsonAsync(
            MobileAuthContract.TokenPath.TrimStart('/'),
            new MobileTokenRequest(code, verifier, options.RedirectUri),
            ContractJson.Options,
            ct);
        if (!response.IsSuccessStatusCode)
            throw new AuthException("Sign-in failed. Please try again.");

        await SaveAsync(await response.Content.ReadFromJsonAsync<TokenResponse>(ContractJson.Options, ct)
            ?? throw new AuthException("Sign-in failed. Please try again."));
    }

    /// <summary>
    /// A valid access token, refreshed if it's about to expire; null once signed out. Network
    /// failures throw and keep the sign-in; only the server refusing the refresh signs out.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        var seen = await LoadAsync();
        if (seen is null)
            return null;
        if (!forceRefresh && !IsExpiring(seen))
            return seen.AccessToken;

        await _refreshLock.WaitAsync(ct);
        try
        {
            var current = _tokens;
            if (current is null)
                return null;
            // Another caller refreshed while this one waited.
            if (!ReferenceEquals(current, seen) && !IsExpiring(current))
                return current.AccessToken;

            using var response = await http.PostAsJsonAsync(
                MobileAuthContract.RefreshPath.TrimStart('/'),
                new RefreshTokenRequest(current.RefreshToken),
                ContractJson.Options,
                ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
            {
                await ClearAsync();
                SignedOut?.Invoke();
                return null;
            }
            response.EnsureSuccessStatusCode();

            var fresh = await response.Content.ReadFromJsonAsync<TokenResponse>(ContractJson.Options, ct)
                ?? throw new HttpRequestException("The refresh response was empty.");
            await SaveAsync(fresh);
            return fresh.AccessToken;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task SignOutAsync()
    {
        var tokens = await LoadAsync();
        if (tokens is not null)
        {
            try
            {
                using var _ = await http.PostAsJsonAsync(
                    MobileAuthContract.SignOutPath.TrimStart('/'),
                    new RefreshTokenRequest(tokens.RefreshToken),
                    ContractJson.Options);
            }
            catch (HttpRequestException)
            {
                // Offline: the local sign-out still happens; the refresh token expires on its own.
            }
        }
        await ClearAsync();
        SignedOut?.Invoke();
    }

    bool IsExpiring(TokenResponse tokens) => tokens.AccessTokenExpiresAt - clock.GetUtcNow() <= RefreshMargin;

    async Task<TokenResponse?> LoadAsync()
    {
        if (!_loaded)
        {
            _tokens = await store.LoadAsync();
            _loaded = true;
        }
        return _tokens;
    }

    async Task SaveAsync(TokenResponse tokens)
    {
        _tokens = tokens;
        _loaded = true;
        await store.SaveAsync(tokens);
    }

    async Task ClearAsync()
    {
        _tokens = null;
        _loaded = true;
        await store.ClearAsync();
    }
}

public sealed class AuthException(string message) : Exception(message);

public static class Pkce
{
    /// <summary>32 random bytes, base64url -- a PKCE verifier (43 characters) or a state value.</summary>
    public static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public static string Challenge(string verifier) => Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
}

/// <summary>Adds the access token to API calls, and on a 401 refreshes once and retries.</summary>
public sealed class AuthHeaderHandler(AuthService auth) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await auth.GetAccessTokenAsync(ct: ct);
        if (token is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await base.SendAsync(request, ct);
        if (response.StatusCode != HttpStatusCode.Unauthorized || token is null)
            return response;

        var fresh = await auth.GetAccessTokenAsync(forceRefresh: true, ct);
        if (fresh is null || fresh == token)
            return response;

        response.Dispose();
        using var retry = new HttpRequestMessage(request.Method, request.RequestUri) { Content = request.Content, Version = request.Version };
        retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", fresh);
        return await base.SendAsync(retry, ct);
    }
}
