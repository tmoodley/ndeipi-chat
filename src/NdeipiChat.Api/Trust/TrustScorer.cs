using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Trust;

/// <summary>What one account adds to a score (SRS §3.2-3.3).</summary>
public sealed record TrustCredit(string Platform, int Tier, decimal Credit);

/// <summary>A score worked out from what counts toward it.</summary>
public sealed record TrustResult(int Score, decimal Tier1, decimal Tier2, decimal Tier3, int Settlements, decimal HistoryPoints, int WorkReferences, decimal WorkPoints, IReadOnlyList<TrustCredit> Counting);

/// <summary>The score's arithmetic, kept free of the database so it's easy to check.</summary>
public static class TrustMath
{
    /// <summary>
    /// §3.3 decay: a linked social account counts fully for <see cref="TrustContract.FullCreditDays"/>
    /// after it was last confirmed, half until <see cref="TrustContract.HalfCreditDays"/>, then not at all.
    /// </summary>
    public static decimal Freshness(DateTimeOffset lastConfirmed, DateTimeOffset now)
    {
        var days = (now - lastConfirmed).TotalDays;
        return days <= TrustContract.FullCreditDays ? 1m : days <= TrustContract.HalfCreditDays ? 0.5m : 0m;
    }

    /// <summary>What a linked account counts for now: tier 1 (a formal identity check) doesn't decay.</summary>
    public static TrustCredit Credit(TrustLink link, DateTimeOffset now)
    {
        var tier = TrustPlatforms.Tier(link.Platform, link.Premium);
        return new TrustCredit(link.Platform, tier, tier == 1 ? 1m : Freshness(link.LastConfirmedAt, now));
    }

    /// <summary>
    /// Each tier gives its weight times its best account's credit (more accounts in a tier don't add up);
    /// each different counterparty someone completed a settlement with in the past year adds a point,
    /// up to <see cref="TrustContract.MaxHistoryPoints"/>; each different client who approved and paid for
    /// their work adds a point, up to <see cref="TrustContract.MaxWorkPoints"/>. The total is capped at 100.
    /// </summary>
    public static TrustResult Compute(bool kycApproved, IEnumerable<TrustLink> links, int settlements, DateTimeOffset now, int workReferences = 0)
    {
        var credits = links.Select(l => Credit(l, now)).ToList();
        if (kycApproved)
            credits.Insert(0, new TrustCredit(TrustPlatforms.Bridge, 1, 1m));
        var counting = credits.Where(c => c.Credit > 0).ToList();

        decimal Best(int tier) => counting.Where(c => c.Tier == tier).Select(c => c.Credit).DefaultIfEmpty(0m).Max();
        var tier1 = TrustContract.Tier1Weight * Best(1);
        var tier2 = TrustContract.Tier2Weight * Best(2);
        var tier3 = TrustContract.Tier3Weight * Best(3);
        var history = (decimal)Math.Min(settlements, TrustContract.MaxHistoryPoints);
        var work = (decimal)Math.Min(workReferences, TrustContract.MaxWorkPoints);
        var score = (int)Math.Round(Math.Min(100m, tier1 + tier2 + tier3 + history + work), MidpointRounding.AwayFromZero);
        return new TrustResult(score, tier1, tier2, tier3, settlements, history, workReferences, work, counting);
    }
}

/// <summary>
/// Works out, keeps and broadcasts people's Trust Scores. It depends only on the database and the
/// hub, so the banking and transfer listeners can use it without a dependency loop.
/// </summary>
public sealed class TrustScorer(ChatDbContext db, ChatNotifier notifier, TimeProvider clock)
{
    /// <summary>A kept score older than this is worked out again when asked for, so decay shows.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(12);

    public async Task<TrustScoreDto> GetAsync(Guid userId, CancellationToken ct)
    {
        var record = await db.TrustScores.AsNoTracking().FirstOrDefaultAsync(s => s.UserId == userId, ct);
        return record is not null && clock.GetUtcNow() - record.UpdatedAt < MaxAge
            ? ToDto(record)
            : await RecomputeAsync(userId, ct);
    }

    /// <summary>Works the score out again, keeps it, and broadcasts it on its topic if it changed.</summary>
    public async Task<TrustScoreDto> RecomputeAsync(Guid userId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var kyc = await db.BankingProfiles.AnyAsync(p => p.UserId == userId && p.KycStatus == BankingProfile.Approved, ct);
        var links = await db.TrustLinks.AsNoTracking().Where(l => l.UserId == userId).ToListAsync(ct);
        var since = now.AddDays(-365);
        var counterparties = await db.BankTransfers
            .Where(t => t.Status == TransferStatuses.Confirmed && t.UpdatedAt >= since && (t.SenderId == userId || t.RecipientId == userId))
            .Select(t => t.SenderId == userId ? t.RecipientId : t.SenderId)
            .Where(other => other != userId)
            .Distinct()
            .CountAsync(ct);

        // Work record: different clients who approved and paid for their work and didn't rate it badly.
        var workSince = now.AddYears(-TrustContract.WorkYears);
        var clients = await db.Gigs
            .Where(g => g.WorkerId == userId && g.Status == GigStatuses.Completed && g.CompletedAt >= workSince && g.ClientId != userId
                && (g.WorkerStars == null || g.WorkerStars >= TrustContract.MinWorkStars))
            .Select(g => g.ClientId)
            .Distinct()
            .CountAsync(ct);

        var result = TrustMath.Compute(kyc, links, counterparties, now, clients);
        var platforms = string.Join(';', result.Counting.Select(c => $"{c.Platform}@{c.Tier}"));

        var record = await db.TrustScores.FirstOrDefaultAsync(s => s.UserId == userId, ct);
        var changed = record is null || record.Score != result.Score || record.Tier1 != result.Tier1 || record.Tier2 != result.Tier2
            || record.Tier3 != result.Tier3 || record.Settlements != result.Settlements || record.WorkReferences != result.WorkReferences
            || record.Platforms != platforms;
        var added = record is null;
        if (record is null)
        {
            if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
                throw new KeyNotFoundException();
            record = new TrustScoreRecord { UserId = userId };
            db.TrustScores.Add(record);
        }
        record.Score = result.Score;
        record.Tier1 = result.Tier1;
        record.Tier2 = result.Tier2;
        record.Tier3 = result.Tier3;
        record.Settlements = result.Settlements;
        record.HistoryPoints = result.HistoryPoints;
        record.WorkReferences = result.WorkReferences;
        record.WorkPoints = result.WorkPoints;
        record.Platforms = platforms;
        record.UpdatedAt = now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (added)
        {
            // Two recomputes raced to add the first record; the other one's is as good.
            db.Entry(record).State = EntityState.Detached;
        }

        var dto = ToDto(record);
        if (changed)
            await notifier.PublishAsync(TrustContract.Topic(userId), dto);
        return dto;
    }

    public static TrustScoreDto ToDto(TrustScoreRecord r)
    {
        var counting = r.Platforms.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('@'))
            .Where(p => p.Length == 2 && int.TryParse(p[1], out _))
            .Select(p => (Platform: p[0], Tier: int.Parse(p[1])))
            .ToList();
        IReadOnlyList<string> In(int tier) => counting.Where(c => c.Tier == tier).Select(c => c.Platform).ToList();
        return new TrustScoreDto(
            r.UserId,
            r.Score,
            TrustContract.Level(r.Score),
            r.Tier1 > 0,
            [
                new TrustTierDto(1, TrustContract.Tier1Weight, r.Tier1, In(1)),
                new TrustTierDto(2, TrustContract.Tier2Weight, r.Tier2, In(2)),
                new TrustTierDto(3, TrustContract.Tier3Weight, r.Tier3, In(3))
            ],
            r.Settlements,
            r.HistoryPoints,
            r.WorkReferences,
            r.WorkPoints,
            r.UpdatedAt);
    }
}
