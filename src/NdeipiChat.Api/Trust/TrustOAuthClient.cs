using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Trust;

/// <summary>Who a platform says the person is, and the tokens to ask it again later.</summary>
public sealed record TrustIdentity(string ExternalId, string? Handle, bool Premium, string? AccessToken, string? RefreshToken, DateTimeOffset? ExpiresAt);

public sealed class TrustProviderException(string message) : Exception(message);

/// <summary>
/// Links LinkedIn (OpenID Connect), X (OAuth 2.0 with PKCE; Premium from <c>verified_type</c>) and
/// Facebook (Login), and asks them again later whether the account is still the person's.
/// </summary>
public sealed class TrustOAuthClient(HttpClient http, IOptions<TrustOptions> options, TimeProvider clock)
{
    public static readonly string[] Platforms = [TrustPlatforms.LinkedIn, TrustPlatforms.X, TrustPlatforms.Facebook];

    OAuthAppOptions? App(string platform) => platform switch
    {
        TrustPlatforms.LinkedIn => options.Value.LinkedIn,
        TrustPlatforms.X => options.Value.X,
        TrustPlatforms.Facebook => options.Value.Facebook,
        _ => null
    };

    public bool IsAvailable(string platform) => App(platform)?.IsConfigured == true;

    /// <summary>Where to send the person to approve the link.</summary>
    public string AuthorizeUrl(string platform, string redirectUri, string state, string codeChallenge)
    {
        var app = App(platform) is { IsConfigured: true } a ? a : throw new TrustProviderException($"{TrustPlatforms.Label(platform)} can't be linked yet.");
        return platform switch
        {
            TrustPlatforms.LinkedIn => Url("https://www.linkedin.com/oauth/v2/authorization",
                ("response_type", "code"), ("client_id", app.ClientId), ("redirect_uri", redirectUri), ("state", state), ("scope", "openid profile")),
            TrustPlatforms.X => Url("https://x.com/i/oauth2/authorize",
                ("response_type", "code"), ("client_id", app.ClientId), ("redirect_uri", redirectUri), ("state", state),
                ("scope", "users.read tweet.read offline.access"), ("code_challenge", codeChallenge), ("code_challenge_method", "S256")),
            _ => Url("https://www.facebook.com/v19.0/dialog/oauth",
                ("client_id", app.ClientId), ("redirect_uri", redirectUri), ("state", state), ("scope", "public_profile"), ("response_type", "code"))
        };
    }

    /// <summary>Trades the callback's code for tokens, then asks who they belong to.</summary>
    public async Task<TrustIdentity> CompleteAsync(string platform, string code, string redirectUri, string codeVerifier, CancellationToken ct)
    {
        var app = App(platform) is { IsConfigured: true } a ? a : throw new TrustProviderException($"{TrustPlatforms.Label(platform)} can't be linked yet.");
        var tokens = platform switch
        {
            TrustPlatforms.LinkedIn => await TokenAsync(HttpMethod.Post, "https://www.linkedin.com/oauth/v2/accessToken", null,
                [("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectUri), ("client_id", app.ClientId), ("client_secret", app.ClientSecret)], ct),
            TrustPlatforms.X => await TokenAsync(HttpMethod.Post, "https://api.x.com/2/oauth2/token", Basic(app),
                [("grant_type", "authorization_code"), ("code", code), ("redirect_uri", redirectUri), ("code_verifier", codeVerifier), ("client_id", app.ClientId)], ct),
            _ => await TokenAsync(HttpMethod.Get, Url("https://graph.facebook.com/v19.0/oauth/access_token",
                ("client_id", app.ClientId), ("redirect_uri", redirectUri), ("client_secret", app.ClientSecret), ("code", code)), null, [], ct)
        };
        return await WhoAsync(platform, tokens, ct)
            ?? throw new TrustProviderException($"{TrustPlatforms.Label(platform)} didn't say whose account it is.");
    }

    /// <summary>
    /// Asks the platform again whose account the kept token is for, refreshing it if it expired and the
    /// platform allows. Null means the platform turned it down (revoked, expired for good).
    /// </summary>
    public async Task<TrustIdentity?> RecheckAsync(string platform, string? accessToken, string? refreshToken, CancellationToken ct)
    {
        var app = App(platform);
        if (app is not { IsConfigured: true })
            return null;
        if (accessToken is not null && await WhoAsync(platform, new Tokens(accessToken, refreshToken, null), ct) is { } who)
            return who;
        if (refreshToken is null || platform != TrustPlatforms.X)
            return null;
        try
        {
            var refreshed = await TokenAsync(HttpMethod.Post, "https://api.x.com/2/oauth2/token", Basic(app),
                [("grant_type", "refresh_token"), ("refresh_token", refreshToken), ("client_id", app.ClientId)], ct);
            return await WhoAsync(platform, refreshed with { Refresh = refreshed.Refresh ?? refreshToken }, ct);
        }
        catch (TrustProviderException)
        {
            return null;
        }
    }

    sealed record Tokens(string Access, string? Refresh, int? ExpiresIn);

    async Task<Tokens> TokenAsync(HttpMethod method, string url, AuthenticationHeaderValue? auth, (string, string)[] form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        if (method == HttpMethod.Post)
            request.Content = new FormUrlEncodedContent(form.Select(f => new KeyValuePair<string, string>(f.Item1, f.Item2)));
        request.Headers.Authorization = auth;
        using var response = await http.SendAsync(request, ct);
        var json = await ReadAsync(response, ct);
        if (!response.IsSuccessStatusCode || json is null || Str(json.Value, "access_token") is not { } access)
            throw new TrustProviderException("The platform didn't accept the link. Please try again.");
        return new Tokens(access, Str(json.Value, "refresh_token"),
            json.Value.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : null);
    }

    async Task<TrustIdentity?> WhoAsync(string platform, Tokens tokens, CancellationToken ct)
    {
        var url = platform switch
        {
            TrustPlatforms.LinkedIn => "https://api.linkedin.com/v2/userinfo",
            TrustPlatforms.X => "https://api.x.com/2/users/me?user.fields=verified_type,username",
            _ => "https://graph.facebook.com/v19.0/me?fields=id,name"
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Access);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            return null;
        var json = await ReadAsync(response, ct);
        if (!response.IsSuccessStatusCode || json is null)
            throw new TrustProviderException("The platform couldn't be reached. Please try again.");

        var expires = tokens.ExpiresIn is { } s ? clock.GetUtcNow().AddSeconds(s) : (DateTimeOffset?)null;
        var root = json.Value;
        switch (platform)
        {
            case TrustPlatforms.LinkedIn:
                return Str(root, "sub") is { } sub ? new TrustIdentity(sub, Str(root, "name"), false, tokens.Access, tokens.Refresh, expires) : null;
            case TrustPlatforms.X:
                if (!root.TryGetProperty("data", out var data) || Str(data, "id") is not { } id)
                    return null;
                // "blue" is X Premium; businesses and governments are verified too, and harder to get.
                var premium = Str(data, "verified_type") is "blue" or "business" or "government";
                return new TrustIdentity(id, Str(data, "username") is { } u ? "@" + u : null, premium, tokens.Access, tokens.Refresh, expires);
            default:
                return Str(root, "id") is { } fb ? new TrustIdentity(fb, Str(root, "name"), false, tokens.Access, tokens.Refresh, expires) : null;
        }
    }

    static async Task<JsonElement?> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? v.ToString()
            : null;

    static AuthenticationHeaderValue Basic(OAuthAppOptions app) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{Uri.EscapeDataString(app.ClientId)}:{Uri.EscapeDataString(app.ClientSecret)}")));

    static string Url(string baseUrl, params (string Key, string Value)[] query) =>
        baseUrl + "?" + string.Join('&', query.Select(q => $"{Uri.EscapeDataString(q.Key)}={Uri.EscapeDataString(q.Value)}"));
}
