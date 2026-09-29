namespace NdeipiChat.Contracts;

/// <summary>
/// Mobile sign-in, OAuth-style: the app opens <see cref="SignInPath"/> in the system browser with a
/// PKCE challenge, Clerk signs the user in there, and the page hands back a one-time code that only
/// the app holding the matching verifier can redeem.
/// </summary>
public static class MobileAuthContract
{
    public const string SignInPath = "/auth/mobile/sign-in";
    public const string TokenPath = "/api/auth/mobile/token";
    public const string RefreshPath = "/api/auth/mobile/refresh";
    public const string SignOutPath = "/api/auth/mobile/sign-out";

    /// <summary>Issues a one-time code for the caller's own Clerk session; the sign-in page calls it, and so does the app's web handoff.</summary>
    public const string CompletePath = "/api/auth/mobile/complete";

    /// <summary>The web shell's sign-in callback, relative to the site: a registered redirect URI.</summary>
    public const string WebCallbackPath = "signin/callback";

    /// <summary>
    /// In the user agent of the app's in-app WebView. Only there does the web callback accept a
    /// handoff whose verifier arrives in the URL fragment (see AuthService.CreateWebHandoffAsync); a
    /// link like that opened in a normal browser can't sign it into someone else's account.
    /// </summary>
    public const string InAppAgentToken = "NdeipiApp/1";

    /// <summary>The web shell asks the app's WebView page to close (its "back" to the launcher).</summary>
    public const string EmbedClosePath = "embed/close";

    /// <summary>
    /// The web shell, in the app's WebView, needs signing in again. The app answers with a fresh
    /// handoff: Clerk's sign-in page can't run inside a WebView (it would bounce to the browser).
    /// </summary>
    public const string EmbedSignInPath = "embed/signin";
}

public sealed record MobileTokenRequest(string Code, string CodeVerifier, string RedirectUri);

public sealed record RefreshTokenRequest(string RefreshToken);

/// <param name="AccessToken">A Clerk session token -- the only bearer token the API accepts.</param>
/// <param name="RefreshToken">Single use: every refresh returns a new one and retires the old.</param>
public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, Guid UserId);
