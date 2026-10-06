using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Ramps;

namespace Ndeipi.Payments.Treasury;

/// <summary>
/// The treasury team's own operations, posted to Ndeipi's house accounts with no integrator scope.
/// Each stands in for the operator console until it exists, as command-line calls (Program.cs).
///
/// - <see cref="DepositAsync"/>: Ndeipi's own money arriving at a provider (capital, or proceeds).
/// - <see cref="BuyPointsAsync"/>: the treasury buys points for itself at the fixed price, so it can
///   pay users who sell NdeipiCoin. The fiat goes into the reserve like any purchase.
/// - <see cref="PostOtcTradeAsync"/>: an OTC trade's two legs, USD through Absa and NdeipiCoin at Blockfinex.
/// </summary>
public sealed class TreasuryService(LedgerService ledger, HouseAccounts house, PointsPricing pricing, IOptions<PaymentsOptions> options)
{
    public const string OtcProvider = "blockfinex_otc";

    public async Task DepositAsync(string provider, string currency, decimal amount, string description, CancellationToken ct)
    {
        Amounts.RequireValid(amount, PointsPricing.FiatDecimals, currency);
        var clearing = await house.ClearingAsync(provider, currency, ct);
        var treasury = await house.TreasuryAsync(currency, ct);
        await ledger.PostAsync(new PostingRequest(description, [new(clearing, -amount), new(treasury, amount)]), ct);
    }

    public async Task<decimal> BuyPointsAsync(string currency, decimal fiat, CancellationToken ct)
    {
        var asset = options.Value.Points.Asset;
        var points = pricing.PointsFor(currency, fiat);
        var treasury = await house.TreasuryAsync(currency, ct);
        var reserve = await house.ReserveAsync(currency, ct);
        var issued = await house.PointsIssuedAsync(asset, ct);
        var treasuryPoints = await house.TreasuryAsync(asset, ct);
        await ledger.PostAsync(new PostingRequest($"Treasury buys {points} points",
            [new(treasury, -fiat), new(reserve, fiat), new(issued, -points), new(treasuryPoints, points)]), ct);
        return points;
    }

    /// <summary>
    /// Buying: USD leaves Ndeipi's Absa account from the treasury and NdeipiCoin arrives at Blockfinex
    /// into the inventory. Selling runs the other way.
    /// </summary>
    public async Task PostOtcTradeAsync(OtcTrade trade, string usdProvider, CancellationToken ct)
    {
        var coin = options.Value.Coin.Asset;
        var usdClearing = await house.ClearingAsync(usdProvider, "usd", ct);
        var treasury = await house.TreasuryAsync("usd", ct);
        var coinClearing = await house.ClearingAsync(OtcProvider, coin, ct);
        var inventory = await house.InventoryAsync(coin, ct);
        var sign = trade.Side == OtcSide.Buy ? 1 : -1;
        await ledger.PostAsync(new PostingRequest($"OTC {trade.Side.ToString().ToLowerInvariant()} {trade.DeskReference}",
        [
            new(treasury, -sign * trade.UsdAmount), new(usdClearing, sign * trade.UsdAmount),
            new(coinClearing, -sign * trade.CoinAmount), new(inventory, sign * trade.CoinAmount)
        ]), ct);
    }
}
