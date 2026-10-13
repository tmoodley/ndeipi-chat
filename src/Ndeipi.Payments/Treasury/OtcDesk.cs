using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Providers;

namespace Ndeipi.Payments.Treasury;

public sealed record OtcTradeEntry(
    OtcSide Side,
    decimal CoinAmount,
    decimal UsdAmount,
    string AbsaReference,
    string DeskReference,
    string RecordedBy,
    DateTimeOffset ExecutedAt);

/// <summary>
/// Blockfinex's OTC desk, which works manually: Ndeipi's treasury team agrees a trade with the
/// desk, settles the USD through Ndeipi's Absa account, and records it here once both legs have
/// landed. The server never calls the desk. The latest trade sets the NdeipiCoin price that
/// conversions are quoted at (<see cref="CoinPricing"/>).
///
/// Recording a trade also posts both legs to Ndeipi's house accounts in the same transaction
/// (<see cref="TreasuryService.PostOtcTradeAsync"/>): a purchase needs the USD in the treasury
/// first, and a sale needs the NdeipiCoin in the inventory.
/// </summary>
public sealed class OtcDesk(PaymentsDbContext db, TreasuryService treasury, ProviderRegistry providers, TimeProvider clock)
{
    public async Task<OtcTrade> RecordAsync(OtcTradeEntry entry, CancellationToken ct)
    {
        if (entry.CoinAmount <= 0 || entry.UsdAmount <= 0)
            throw new ArgumentException("A trade moves a positive amount of both NdeipiCoin and USD.");
        if (string.IsNullOrWhiteSpace(entry.AbsaReference) || string.IsNullOrWhiteSpace(entry.DeskReference) || string.IsNullOrWhiteSpace(entry.RecordedBy))
            throw new ArgumentException("A trade needs its Absa reference, the desk's reference and who recorded it.");
        var now = clock.GetUtcNow();
        if (entry.ExecutedAt > now)
            throw new ArgumentException("A trade is recorded after it settles, not before.");

        var trade = new OtcTrade
        {
            Side = entry.Side,
            CoinAmount = entry.CoinAmount,
            UsdAmount = entry.UsdAmount,
            UsdPerCoin = entry.UsdAmount / entry.CoinAmount,
            AbsaReference = entry.AbsaReference.Trim(),
            DeskReference = entry.DeskReference.Trim(),
            RecordedBy = entry.RecordedBy.Trim(),
            ExecutedAt = entry.ExecutedAt,
            RecordedAt = now
        };
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.OtcTrades.Add(trade);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(trade).State = EntityState.Detached;
            throw new InvalidOperationException($"Desk trade {trade.DeskReference} is already recorded.");
        }
        // The USD leg settles through Ndeipi's Absa account.
        await treasury.PostOtcTradeAsync(trade, providers.RailFor(RailCodes.AbsaEft).Name, ct);
        await transaction.CommitAsync(ct);
        return trade;
    }

    /// <summary>The most recently executed trade, which sets the price; null before the first.</summary>
    public Task<OtcTrade?> LatestAsync(CancellationToken ct) =>
        db.OtcTrades.AsNoTracking().OrderByDescending(t => t.ExecutedAt).ThenByDescending(t => t.Id).FirstOrDefaultAsync(ct);
}
