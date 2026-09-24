using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Auth;

/// <summary>
/// Signs the app in with Clerk. Clerk has no .NET MAUI SDK, so the app signs in through Clerk's
/// own web components in the system browser and receives a one-time code (PKCE-protected, so an app
/// hijacking the redirect scheme can't redeem it). The code buys a Clerk session token plus a
/// rotating refresh token; refreshing asks Clerk to mint a new session token, so ending the Clerk
/// session ends the app's sign-in too.
/// </summary>
public sealed class MobileAuthService(
    ChatDbContext db,
    IClerkBackendApi clerk,
    IOptions<ClerkOptions> clerkOptions,
    IOptions<MobileAuthOptions> options,
    TimeProvider clock,
    ILogger<MobileAuthService> log)
{
    public bool IsAllowedRedirect(string? uri) =>
        uri is not null && options.Value.RedirectUris.Contains(uri, StringComparer.Ordinal);

    /// <summary>An S256 challenge: a SHA-256 hash, base64url-encoded without padding.</summary>
    public static bool IsValidChallenge(string? challenge) =>
        challenge is { Length: 43 } && challenge.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public async Task<string> IssueCodeAsync(User user, string sessionId, string redirectUri, string challenge, CancellationToken ct)
    {
        var code = NewSecret();
        db.AuthCodes.Add(new MobileAuthCode
        {
            CodeHash = Hash(code),
            UserId = user.Id,
            ClerkSessionId = sessionId,
            RedirectUri = redirectUri,
            CodeChallenge = challenge,
            ExpiresAt = clock.GetUtcNow() + options.Value.CodeLifetime
        });
        await db.SaveChangesAsync(ct);
        return code;
    }

    public async Task<TokenResponse?> RedeemCodeAsync(MobileTokenRequest request, CancellationToken ct)
    {
        var hash = Hash(request.Code ?? "");
        var entry = await db.AuthCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == hash, ct);
        if (entry is null)
            return null;

        // Delete before checking anything: whatever happens, a code is spent after one attempt,
        // and of two racing requests only the one whose delete succeeds carries on.
        if (await db.AuthCodes.Where(c => c.CodeHash == hash).ExecuteDeleteAsync(ct) == 0)
            return null;

        if (entry.ExpiresAt < clock.GetUtcNow()
            || entry.RedirectUri != request.RedirectUri
            || !MatchesChallenge(request.CodeVerifier ?? "", entry.CodeChallenge))
            return null;

        try
        {
            return await IssueTokensAsync(entry.UserId, entry.ClerkSessionId, ct);
        }
        catch (ClerkSessionEndedException)
        {
            return null;
        }
    }

    public async Task<TokenResponse?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken ?? "");
        var token = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        var now = clock.GetUtcNow();
        if (token is null || token.ExpiresAt < now)
            return null;

        if (token.RevokedAt is not null)
        {
            // A retired token came back: someone else holds a copy. End the whole sign-in.
            log.LogWarning("Refresh token reuse for Clerk session {SessionId}; signing it out", token.ClerkSessionId);
            await EndSessionAsync(token.ClerkSessionId, ct);
            return null;
        }

        var retired = await db.RefreshTokens
            .Where(t => t.Id == token.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        if (retired == 0)
            return null;

        try
        {
            return await IssueTokensAsync(token.UserId, token.ClerkSessionId, ct);
        }
        catch (ClerkSessionEndedException)
        {
            await RevokeSessionTokensAsync(token.ClerkSessionId, ct);
            return null;
        }
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken ?? "");
        var token = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null)
            await EndSessionAsync(token.ClerkSessionId, ct);
    }

    async Task<TokenResponse> IssueTokensAsync(Guid userId, string sessionId, CancellationToken ct)
    {
        var jwt = await clerk.CreateSessionTokenAsync(sessionId, clerkOptions.Value.SessionTokenTemplate, ct);
        var expiresAt = new DateTimeOffset(new JsonWebToken(jwt).ValidTo, TimeSpan.Zero);

        var refresh = NewSecret();
        var now = clock.GetUtcNow();
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            TokenHash = Hash(refresh),
            UserId = userId,
            ClerkSessionId = sessionId,
            CreatedAt = now,
            ExpiresAt = now + options.Value.RefreshTokenLifetime
        });
        await db.SaveChangesAsync(ct);

        return new TokenResponse(jwt, expiresAt, refresh, userId);
    }

    async Task EndSessionAsync(string sessionId, CancellationToken ct)
    {
        await RevokeSessionTokensAsync(sessionId, ct);
        try
        {
            await clerk.RevokeSessionAsync(sessionId, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Couldn't revoke Clerk session {SessionId}", sessionId);
        }
    }

    Task RevokeSessionTokensAsync(string sessionId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.RefreshTokens
            .Where(t => t.ClerkSessionId == sessionId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    static string NewSecret() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    static bool MatchesChallenge(string verifier, string challenge)
    {
        var computed = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(computed), Encoding.ASCII.GetBytes(challenge));
    }
}
