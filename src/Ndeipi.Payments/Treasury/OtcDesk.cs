using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Data;

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
/// Recording a trade does not post to the ledger yet. The postings (USD out of Treasury, NdeipiCoin
/// into Inventory) belong to Ndeipi's house accounts, which no integrator owns; the ledger is
/// scoped per integrator today, and house accounts arrive with conversions in M4.
/// </summary>
public sealed class OtcDesk(PaymentsDbContext db, TimeProvider clock)
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
        return trade;
    }

    /// <summary>The most recently executed trade, which sets the price; null before the first.</summary>
    public Task<OtcTrade?> LatestAsync(CancellationToken ct) =>
        db.OtcTrades.AsNoTracking().OrderByDescending(t => t.ExecutedAt).ThenByDescending(t => t.Id).FirstOrDefaultAsync(ct);
}
