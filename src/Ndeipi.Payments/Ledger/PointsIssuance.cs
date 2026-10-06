using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Ledger;

/// <summary>
/// Puts Ndeipi Points into wallets. Every point credited has a matching entry in the integrator's
/// PointsIssued account for its kind, so the points in circulation are always known:
///
/// - **Purchased** points land in a wallet's available bucket and can be cashed out. On-ramps credit
///   them (M4), once the fiat is in the PointsReserve.
/// - **Earned** points (rewards, formerly Believe Points) land in the earned bucket and can be spent
///   and sent but never cashed out.
/// </summary>
public sealed class PointsIssuance(PaymentsDbContext db, LedgerService ledger, IOptions<PaymentsOptions> options)
{
    string Asset => options.Value.Points.Asset;

    public Task<Posting> CreditEarnedAsync(string walletId, decimal amount, string description, CancellationToken ct) =>
        CreditAsync(walletId, amount, LedgerBucket.Earned, description, transferId: null, ct);

    public Task<Posting> CreditPurchasedAsync(string walletId, decimal amount, string description, string? transferId, CancellationToken ct) =>
        CreditAsync(walletId, amount, LedgerBucket.Available, description, transferId, ct);

    async Task<Posting> CreditAsync(string walletId, decimal amount, LedgerBucket bucket, string description, string? transferId, CancellationToken ct)
    {
        var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == walletId, ct) ?? throw PaymentsException.NotFound("wallet");
        if (wallet.Asset != Asset)
            throw new InvalidOperationException("Only points wallets are credited with points.");
        Amounts.RequireValid(amount, wallet.Decimals, wallet.Asset);

        var target = await db.LedgerAccounts.AsNoTracking().SingleAsync(a => a.WalletId == walletId && a.Bucket == bucket, ct);
        var issued = await IssuedAccountAsync(bucket, ct);
        return await ledger.PostAsync(new PostingRequest(description, [new(issued, -amount), new(target.Id, amount)], transferId), ct);
    }

    /// <summary>The integrator's PointsIssued account for one kind of point, opened on first use.</summary>
    public async Task<string> IssuedAccountAsync(LedgerBucket bucket, CancellationToken ct)
    {
        var existing = await FindAsync(bucket, ct);
        if (existing is not null)
            return existing;
        try
        {
            return (await ledger.OpenAccountAsync(LedgerAccountKind.PointsIssued, bucket, Asset, walletId: null, ct)).Id;
        }
        catch (DbUpdateException)
        {
            // Opened at the same moment by another request.
            foreach (var entry in db.ChangeTracker.Entries<LedgerAccount>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return await FindAsync(bucket, ct) ?? throw new InvalidOperationException("PointsIssued account could not be opened.");
        }
    }

    Task<string?> FindAsync(LedgerBucket bucket, CancellationToken ct) =>
        db.LedgerAccounts.AsNoTracking()
            .Where(a => a.Kind == LedgerAccountKind.PointsIssued && a.Bucket == bucket && a.Asset == Asset && a.WalletId == null)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);
}
