using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Trust;

/// <summary>
/// The Trust Score's API: anyone's score, the owner's own page, and linking and unlinking accounts.
/// The arithmetic and broadcasting are <see cref="TrustScorer"/>'s.
/// </summary>
public sealed class TrustService(
    ChatDbContext db,
    TrustScorer scorer,
    TrustOAuthClient oauth,
    TrustTokenProtector tokens,
    BankingService banking,
    CurrentUserService users,
    IOptions<TrustOptions> options,
    IOptions<BridgeOptions> bridge,
    IMemoryCache cache,
    TimeProvider clock,
    ILogger<TrustService> log)
{
    static readonly TimeSpan LinkWindow = TimeSpan.FromMinutes(10);
    static readonly TimeSpan RefreshCooldown = TimeSpan.FromSeconds(30);

    /// <summary>A link someone started: kept until the platform sends them back, keyed by its state.</summary>
    sealed record PendingLink(Guid UserId, string Platform, string Verifier, string RedirectUri);

    public async Task<TrustScoreDto?> ScoreAsync(Guid userId, CancellationToken ct)
    {
        try
        {
            return await scorer.GetAsync(userId, ct);
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    public TrustSettingsDto Settings() =>
        new(options.Value.Telegram.IsConfigured ? options.Value.Telegram.BotUsername : null);

    public async Task<MyTrustDto> MeAsync(User user, CancellationToken ct)
    {
        var score = await scorer.RecomputeAsync(user.Id, ct);
        var now = clock.GetUtcNow();
        var links = await db.TrustLinks.AsNoTracking().Where(l => l.UserId == user.Id).ToListAsync(ct);
        var kyc = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => p.KycStatus).FirstOrDefaultAsync(ct);

        TrustLinkDto Row(string platform)
        {
            var label = TrustPlatforms.Label(platform);
            switch (platform)
            {
                case TrustPlatforms.Bridge:
                    var approved = kyc == BankingProfile.Approved;
                    return new TrustLinkDto(platform, label, 1, bridge.Value.IsConfigured, approved, null, null, null, approved ? 1m : 0m,
                        approved ? "Your identity has been checked." : "Verify your identity under Wallet: photo ID and a selfie.");
                case TrustPlatforms.Mpost:
                    return new TrustLinkDto(platform, label, 1, false, false, null, null, null, 0m, "Coming soon.");
                case TrustPlatforms.WhatsApp:
                    return new TrustLinkDto(platform, label, 3, false, false, null, null, null, 0m, "Coming soon.");
            }

            var available = platform == TrustPlatforms.Telegram ? options.Value.Telegram.IsConfigured : oauth.IsAvailable(platform);
            var link = links.FirstOrDefault(l => l.Platform == platform);
            var credit = link is null ? 0m : TrustMath.Credit(link, now).Credit;
            var note = link is null
                ? available
                    ? platform == TrustPlatforms.X ? "Premium accounts count in tier 2." : null
                    : "Not available yet."
                : link.ViaSignIn
                    ? credit == 0m ? "No longer counts: sign in to Ndeipi to confirm it again." : "From your sign-in: kept up to date as you use Ndeipi."
                    : credit == 0m
                        ? "No longer counts: link it again to confirm it's still yours."
                        : credit < 1m
                            ? "Counts half: link it again to confirm it's still yours."
                            : platform == TrustPlatforms.X ? link.Premium ? "Premium: counts in tier 2." : "Counts in tier 3; Premium counts in tier 2." : null;
            return new TrustLinkDto(platform, label, TrustPlatforms.Tier(platform, link?.Premium == true), available || link?.ViaSignIn == true, link is not null,
                link?.Handle, link?.LinkedAt, link?.LastConfirmedAt, credit, note, link?.ViaSignIn == true);
        }

        // Sign-in-only accounts (Google, Apple, a verified phone…) are listed once they count.
        var signIn = links.Where(l => TrustPlatforms.SignInOnly.Contains(l.Platform))
            .OrderBy(l => TrustPlatforms.SignInOnly.ToList().IndexOf(l.Platform))
            .Select(l =>
            {
                var credit = TrustMath.Credit(l, now).Credit;
                return new TrustLinkDto(l.Platform, TrustPlatforms.Label(l.Platform), TrustPlatforms.Tier(l.Platform), true, true, l.Handle, l.LinkedAt, l.LastConfirmedAt,
                    credit, credit == 0m ? "No longer counts: sign in to Ndeipi to confirm it again." : "From your sign-in: kept up to date as you use Ndeipi.", true);
            });

        return new MyTrustDto(score, TrustPlatforms.All.Select(Row).Concat(signIn).ToList());
    }

    /// <summary>Starts linking an OAuth account: where to send the person.</summary>
    public TrustLinkStartDto StartLink(User user, string platform, Uri site)
    {
        if (!TrustOAuthClient.Platforms.Contains(platform) || !oauth.IsAvailable(platform))
            throw new ChatRejectedException($"{TrustPlatforms.Label(platform)} can't be linked yet.");

        var state = Random(32);
        var verifier = Random(48);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirect = CallbackUrl(site, platform);
        cache.Set(StateKey(state), new PendingLink(user.Id, platform, verifier, redirect), LinkWindow);
        return new TrustLinkStartDto(oauth.AuthorizeUrl(platform, redirect, state, challenge));
    }

    /// <summary>The platform sent the person back: link the account. Returns what to tell them (null if it worked).</summary>
    public async Task<string?> CompleteLinkAsync(string platform, string? code, string? state, string? error, CancellationToken ct)
    {
        if (state is null || !cache.TryGetValue(StateKey(state), out PendingLink? pending) || pending is null || pending.Platform != platform)
            return "That link has expired. Please try again.";
        cache.Remove(StateKey(state));
        if (error is not null || code is null)
            return $"{TrustPlatforms.Label(platform)} wasn't linked.";

        try
        {
            var identity = await oauth.CompleteAsync(platform, code, pending.RedirectUri, pending.Verifier, ct);
            await SaveLinkAsync(pending.UserId, platform, identity, ct);
            return null;
        }
        catch (TrustProviderException ex)
        {
            return ex.Message;
        }
        catch (ChatRejectedException ex)
        {
            return ex.Message;
        }
        catch (HttpRequestException ex)
        {
            log.LogWarning(ex, "Couldn't reach {Platform} to finish a trust link", platform);
            return $"{TrustPlatforms.Label(platform)} couldn't be reached. Please try again.";
        }
    }

    public async Task<MyTrustDto> LinkTelegramAsync(User user, TelegramLoginRequest login, CancellationToken ct)
    {
        var telegram = options.Value.Telegram;
        if (!telegram.IsConfigured)
            throw new ChatRejectedException("Telegram can't be linked yet.");
        if (!TelegramLogin.IsValid(login, telegram.BotToken, clock.GetUtcNow()))
            throw new ChatRejectedException("Telegram couldn't confirm that account. Please try again.");

        var handle = login.Username is { Length: > 0 } u ? "@" + u : string.Join(' ', new[] { login.FirstName, login.LastName }.Where(n => !string.IsNullOrEmpty(n)));
        await SaveLinkAsync(user.Id, TrustPlatforms.Telegram,
            new TrustIdentity(login.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), handle.Length > 0 ? handle : null, false, null, null, null), ct);
        return await MeAsync(user, ct);
    }

    public async Task<MyTrustDto> UnlinkAsync(User user, string platform, CancellationToken ct)
    {
        if (await db.TrustLinks.Where(l => l.UserId == user.Id && l.Platform == platform && !l.ViaSignIn).ExecuteDeleteAsync(ct) == 0
            && await db.TrustLinks.AnyAsync(l => l.UserId == user.Id && l.Platform == platform, ct))
            throw new ChatRejectedException($"{TrustPlatforms.Label(platform)} comes from how you sign in to Ndeipi. Remove it from your sign-in account to stop it counting.");
        await scorer.RecomputeAsync(user.Id, ct);
        return await MeAsync(user, ct);
    }

    /// <summary>
    /// Checks again (at most every 30 seconds): the sign-in accounts and phone from Clerk, so one added
    /// since the last profile sync counts now, and the identity check from Bridge; then the score.
    /// </summary>
    public async Task<MyTrustDto> RefreshAsync(User user, CancellationToken ct)
    {
        if (!cache.TryGetValue(RefreshKey(user.Id), out _))
        {
            cache.Set(RefreshKey(user.Id), true, RefreshCooldown);
            user = await users.GetByClerkIdAsync(user.ClerkUserId, ct, resync: true);
            if (bridge.Value.IsConfigured)
            {
                try
                {
                    await banking.RefreshAsync(user.Id, ct);
                }
                catch (Exception ex) when (ex is BridgeApiException or HttpRequestException)
                {
                    log.LogWarning(ex, "Couldn't refresh KYC for {UserId} for the Trust Score", user.Id);
                }
            }
        }
        return await MeAsync(user, ct);
    }

    /// <summary>
    /// The anti-sybil rule: an outside account backs one Ndeipi account. Linking a platform again
    /// replaces the person's earlier account there, and confirms it afresh.
    /// </summary>
    async Task SaveLinkAsync(Guid userId, string platform, TrustIdentity identity, CancellationToken ct)
    {
        var label = TrustPlatforms.Label(platform);
        if (await db.TrustLinks.AnyAsync(l => l.Platform == platform && l.ExternalId == identity.ExternalId && l.UserId != userId, ct))
            throw new ChatRejectedException($"That {label} account is already linked to another Ndeipi account.");

        var now = clock.GetUtcNow();
        var link = await db.TrustLinks.FirstOrDefaultAsync(l => l.UserId == userId && l.Platform == platform, ct);
        if (link is null || link.ExternalId != identity.ExternalId)
        {
            if (link is not null)
                db.TrustLinks.Remove(link);
            link = new TrustLink { Id = Guid.NewGuid(), UserId = userId, Platform = platform, ExternalId = identity.ExternalId, LinkedAt = now };
            db.TrustLinks.Add(link);
        }
        // Linked here now: re-checked through its tokens rather than by signing in.
        link.ViaSignIn = false;
        Apply(link, identity, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ChatRejectedException($"That {label} account is already linked to another Ndeipi account.");
        }
        await scorer.RecomputeAsync(userId, ct);
    }

    void Apply(TrustLink link, TrustIdentity identity, DateTimeOffset now)
    {
        link.Handle = identity.Handle is { Length: > 120 } h ? h[..120] : identity.Handle;
        link.Premium = identity.Premium;
        link.AccessTokenProtected = tokens.Protect(identity.AccessToken);
        link.RefreshTokenProtected = tokens.Protect(identity.RefreshToken);
        link.TokenExpiresAt = identity.ExpiresAt;
        link.LastConfirmedAt = now;
    }

    /// <summary>
    /// The daily re-check: asks each platform whether linked accounts are still the person's (confirming
    /// them afresh), then works out everyone's score again so decay shows.
    /// </summary>
    public async Task RecheckAllAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = now - options.Value.RecheckInterval;
        var links = await db.TrustLinks.Where(l => l.AccessTokenProtected != null && l.LastConfirmedAt < due).ToListAsync(ct);
        foreach (var link in links)
        {
            try
            {
                var identity = await oauth.RecheckAsync(link.Platform, tokens.Unprotect(link.AccessTokenProtected), tokens.Unprotect(link.RefreshTokenProtected), ct);
                if (identity is not null && identity.ExternalId == link.ExternalId)
                {
                    Apply(link, identity, now);
                }
                else
                {
                    // Revoked or expired for good: it decays from its last confirmation until they link it again.
                    link.AccessTokenProtected = link.RefreshTokenProtected = null;
                }
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex) when (ex is TrustProviderException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                log.LogWarning(ex, "Couldn't re-check {Platform} link {LinkId}; will try again", link.Platform, link.Id);
            }
        }

        var people = await db.TrustLinks.Select(l => l.UserId)
            .Union(db.TrustScores.Where(s => s.UpdatedAt < now - TrustScorer.MaxAge).Select(s => s.UserId))
            .Distinct()
            .ToListAsync(ct);
        foreach (var userId in people)
            await scorer.RecomputeAsync(userId, ct);
    }

    Uri BaseFor(Uri site) => options.Value.PublicBaseUrl.Length > 0 ? new Uri(options.Value.PublicBaseUrl.TrimEnd('/') + "/") : site;

    string CallbackUrl(Uri site, string platform) => new Uri(BaseFor(site), $"{TrustContract.BasePath}/oauth/{platform}/callback").ToString();

    /// <summary>Where the callback sends the person back to: the trust page, saying how it went.</summary>
    public string ReturnUrl(Uri site, string platform, string? problem) =>
        new Uri(BaseFor(site), problem is null
            ? $"{TrustContract.PagePath}?linked={Uri.EscapeDataString(platform)}"
            : $"{TrustContract.PagePath}?error={Uri.EscapeDataString(problem)}").ToString();

    static string StateKey(string state) => "trust-link:" + state;
    static string RefreshKey(Guid userId) => "trust-refresh:" + userId;
    static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));
    static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Checks what Telegram's login widget hands back (core.telegram.org/widgets/login).</summary>
public static class TelegramLogin
{
    /// <summary>How long after Telegram signs it a login still counts.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(1);

    public static string DataCheckString(TelegramLoginRequest login)
    {
        var fields = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_date"] = login.AuthDate.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["id"] = login.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };
        if (login.FirstName is not null) fields["first_name"] = login.FirstName;
        if (login.LastName is not null) fields["last_name"] = login.LastName;
        if (login.Username is not null) fields["username"] = login.Username;
        if (login.PhotoUrl is not null) fields["photo_url"] = login.PhotoUrl;
        return string.Join('\n', fields.Select(f => $"{f.Key}={f.Value}"));
    }

    /// <summary>The hash is HMAC-SHA256 of the sorted fields, keyed with SHA-256 of the bot's token.</summary>
    public static string Sign(TelegramLoginRequest login, string botToken) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(SHA256.HashData(Encoding.UTF8.GetBytes(botToken)), Encoding.UTF8.GetBytes(DataCheckString(login))));

    public static bool IsValid(TelegramLoginRequest login, string botToken, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(login.Hash) || login.Hash.Length != 64)
            return false;
        var signedAt = DateTimeOffset.FromUnixTimeSeconds(login.AuthDate);
        if (now - signedAt > MaxAge || signedAt - now > TimeSpan.FromMinutes(5))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(Sign(login, botToken)),
            Encoding.ASCII.GetBytes(login.Hash.ToLowerInvariant()));
    }
}
