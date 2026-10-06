using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Ledger;

/// <summary>
/// Ndeipi's own ledger accounts, opened on first use (docs/payments-api/README.md):
///
/// - <b>Clearing</b>, per provider and currency: the float at Absa or PayPal, reconciled against
///   that provider's statement (SRV-PROV-06).
/// - <b>Reserve</b>, per currency: fiat held 1:1 against purchased points. Never spent on anything
///   else, and never negative: a cash-out it cannot cover is refused.
/// - <b>Treasury</b>: Ndeipi's own money per currency, and its own points, which conversions to
///   NdeipiCoin pay into and conversions back pay out of.
/// - <b>Inventory</b>: Ndeipi's NdeipiCoin stock, from the OTC desk.
/// - <b>Fees</b>, per asset: Ndeipi's spread.
/// </summary>
public sealed class HouseAccounts(PaymentsDbContext db, LedgerService ledger)
{
    public Task<string> ClearingAsync(string provider, string currency, CancellationToken ct) =>
        AccountAsync(LedgerAccountKind.ProviderClearing, LedgerBucket.Available, currency, provider, ct);

    public Task<string> ReserveAsync(string currency, CancellationToken ct) =>
        AccountAsync(LedgerAccountKind.PointsReserve, LedgerBucket.Available, currency, null, ct);

    public Task<string> TreasuryAsync(string asset, CancellationToken ct) =>
        AccountAsync(LedgerAccountKind.Treasury, LedgerBucket.Available, asset, null, ct);

    public Task<string> InventoryAsync(string asset, CancellationToken ct) =>
        AccountAsync(LedgerAccountKind.Inventory, LedgerBucket.Available, asset, null, ct);

    public Task<string> FeesAsync(string asset, CancellationToken ct) =>
        AccountAsync(LedgerAccountKind.FeeRevenue, LedgerBucket.Available, asset, null, ct);

    /// <summary>Points the house itself issued, when the treasury buys points for its own account.</summary>
    public Task<string> PointsIssuedAsync(string asset, CancellationToken ct) =>
        AccountAsync(LedgerAccountKind.PointsIssued, LedgerBucket.Available, asset, null, ct);

    public Task<string> SuspenseAsync(string currency, CancellationToken ct) =>
        AccountAsync(LedgerAccountKind.Suspense, LedgerBucket.Pending, currency, null, ct);

    public Task<decimal> BalanceAsync(string accountId, CancellationToken ct) =>
        db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.Id == accountId).Select(a => a.Balance).SingleAsync(ct);

    async Task<string> AccountAsync(LedgerAccountKind kind, LedgerBucket bucket, string asset, string? provider, CancellationToken ct)
    {
        if (await FindAsync(kind, bucket, asset, provider, ct) is { } existing)
            return existing;
        try
        {
            return (await ledger.OpenHouseAccountAsync(kind, bucket, asset, ct, provider)).Id;
        }
        catch (DbUpdateException)
        {
            // Opened at the same moment by another request; the unique indexes keep it to one.
            foreach (var entry in db.ChangeTracker.Entries<LedgerAccount>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return await FindAsync(kind, bucket, asset, provider, ct) ?? throw new InvalidOperationException($"House {kind} account could not be opened.");
        }
    }

    Task<string?> FindAsync(LedgerAccountKind kind, LedgerBucket bucket, string asset, string? provider, CancellationToken ct) =>
        db.LedgerAccounts.IgnoreQueryFilters().AsNoTracking()
            .Where(a => a.IntegratorId == IntegratorScope.House && a.Kind == kind && a.Bucket == bucket && a.Asset == asset && a.Provider == provider && a.WalletId == null)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct);
}
