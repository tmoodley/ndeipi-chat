using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Gigs;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Trust;

/// <summary>
/// The accounts someone signs in to Ndeipi with (Google, Apple, Microsoft… through Clerk) and their
/// verified phone count toward their Trust Score with nothing to link: each profile sync from Clerk
/// confirms them afresh, and one removed from their sign-in stops counting. An account linked on
/// the trust page for the same platform wins over the sign-in one.
/// </summary>
public sealed class TrustSignInSync(ChatDbContext db, TrustScorer scorer, TimeProvider clock, ILogger<TrustSignInSync> log) : IUserProfileListener
{
    public async Task ProfileSyncedAsync(User user, ClerkUser profile, CancellationToken ct)
    {
        try
        {
            await SyncAsync(user, profile, ct);
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            // Never in the way of signing in: the next sync tries again.
            log.LogWarning(ex, "Couldn't sync sign-in accounts to the Trust Score for {UserId}", user.Id);
        }
    }

    async Task SyncAsync(User user, ClerkUser profile, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var wanted = profile.VerifiedExternalAccounts
            .Select(a => (Platform: TrustPlatforms.FromSignInProvider(a.Provider), ExternalId: a.ProviderUserId!, Handle: HandleFor(a)))
            .Where(a => a.Platform is not null)
            .GroupBy(a => a.Platform!)
            .Select(g => (Platform: g.Key, g.First().ExternalId, g.First().Handle))
            .ToList();
        if (user.Phone is { Length: > 0 } phone)
            wanted.Add((TrustPlatforms.Phone, phone, "•••• " + phone[^Math.Min(4, phone.Length)..]));

        var links = await db.TrustLinks.Where(l => l.UserId == user.Id).ToListAsync(ct);
        var changed = false;

        // Gone from their sign-in (or replaced by another account on the same platform).
        foreach (var stale in links.Where(l => l.ViaSignIn && !wanted.Any(w => w.Platform == l.Platform && w.ExternalId == l.ExternalId)).ToList())
        {
            db.TrustLinks.Remove(stale);
            links.Remove(stale);
            changed = true;
        }

        foreach (var (platform, externalId, handle) in wanted)
        {
            var link = links.FirstOrDefault(l => l.Platform == platform);
            if (link is not null)
            {
                // Theirs already, from signing in or from the trust page: still theirs as of now.
                if (link.ExternalId == externalId)
                {
                    link.LastConfirmedAt = now;
                    if (link.ViaSignIn)
                        link.Handle = handle;
                    changed = true;
                }
                continue;
            }

            if (await db.TrustLinks.AnyAsync(l => l.Platform == platform && l.ExternalId == externalId && l.UserId != user.Id, ct))
            {
                log.LogInformation("{Platform} sign-in account of {UserId} already backs another account; not counted", platform, user.Id);
                continue;
            }
            db.TrustLinks.Add(new TrustLink
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Platform = platform,
                ExternalId = externalId,
                Handle = handle,
                ViaSignIn = true,
                LinkedAt = now,
                LastConfirmedAt = now
            });
            changed = true;
        }

        if (!changed)
            return;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another account claimed the same outside account a moment earlier.
            foreach (var entry in db.ChangeTracker.Entries<TrustLink>().ToList())
                entry.State = EntityState.Detached;
            throw;
        }
        await scorer.RecomputeAsync(user.Id, ct);
    }

    static string? HandleFor(ClerkExternalAccount account)
    {
        var handle = account.EmailAddress ?? account.Username;
        return handle is { Length: > 120 } ? handle[..120] : handle;
    }
}

/// <summary>Work record: a gig of theirs was approved, or the client rated it.</summary>
public sealed class TrustWorkListener(TrustScorer scorer) : IWorkRecordListener
{
    public async Task WorkRecordChangedAsync(Guid workerId, CancellationToken ct) => await scorer.RecomputeAsync(workerId, ct);
}
