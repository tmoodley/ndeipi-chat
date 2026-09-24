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
}

public sealed record MobileTokenRequest(string Code, string CodeVerifier, string RedirectUri);

public sealed record RefreshTokenRequest(string RefreshToken);

/// <param name="AccessToken">A Clerk session token -- the only bearer token the API accepts.</param>
/// <param name="RefreshToken">Single use: every refresh returns a new one and retires the old.</param>
public sealed record TokenResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, Guid UserId);
