using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Treasury;

/// <summary>A priced conversion: pay <see cref="Amount"/> of <see cref="From"/>, of which <see cref="Fee"/> is Ndeipi's spread, and receive <see cref="AmountOut"/> of <see cref="To"/>.</summary>
public sealed record CoinQuote(string From, string To, decimal Amount, decimal Fee, decimal AmountOut, decimal Rate);

/// <summary>
/// Prices conversions between Ndeipi Points and NdeipiCoin. The NdeipiCoin price is the USD price
/// of the last OTC trade (<see cref="OtcDesk"/>), turned into points through the points' own USD
/// price (<c>Payments:Points:Prices:usd</c>).
///
/// The spread is charged in the asset paid and rounds up; what is received rounds down. So a
/// conversion and its reverse always leave the user with no more than they started with, and
/// Ndeipi's stock and treasury never pay out more than came in.
/// </summary>
public sealed class CoinPricing(OtcDesk desk, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    PaymentsOptions Options => options.Value;

    /// <summary>Points per NdeipiCoin at the last OTC price, before the spread.</summary>
    public async Task<decimal> PointsPerCoinAsync(CancellationToken ct)
    {
        if (!Options.Points.Prices.TryGetValue("usd", out var usdPerPoint) || usdPerPoint <= 0)
            throw Unpriced("Ndeipi Points have no USD price, which NdeipiCoin is priced through.");
        var trade = await desk.LatestAsync(ct) ?? throw Unpriced("No OTC trade has been recorded yet.");
        if (clock.GetUtcNow() - trade.ExecutedAt > Options.Coin.MaxPriceAge)
            throw Unpriced("The last OTC trade is too old to price from.");
        return trade.UsdPerCoin / usdPerPoint;
    }

    /// <summary>Quotes paying <paramref name="amount"/> of <paramref name="from"/>: points to buy NdeipiCoin, or NdeipiCoin to sell it.</summary>
    public async Task<CoinQuote> QuoteAsync(string from, decimal amount, CancellationToken ct)
    {
        var points = Options.Points;
        var coin = Options.Coin;
        var pointsPerCoin = await PointsPerCoinAsync(ct);

        if (from == points.Asset)
            return Price(points.Asset, coin.Asset, amount, points.Decimals, coin.Decimals, 1 / pointsPerCoin, coin.SpreadBasisPoints);
        if (from == coin.Asset)
            return Price(coin.Asset, points.Asset, amount, coin.Decimals, points.Decimals, pointsPerCoin, coin.SpreadBasisPoints);
        throw PaymentsException.Unprocessable("unsupported_route", $"{from} does not convert to NdeipiCoin or Ndeipi Points.", "source_wallet_id");
    }

    /// <summary>The conversion arithmetic, kept pure so its rounding can be tested on its own.</summary>
    public static CoinQuote Price(string from, string to, decimal amount, int fromDecimals, int toDecimals, decimal toPerFrom, int spreadBasisPoints)
    {
        if (amount <= 0)
            throw PaymentsException.Unprocessable("amount_below_minimum", "Convert a positive amount.", "amount");
        if (decimal.Round(amount, fromDecimals) != amount)
            throw PaymentsException.Unprocessable("invalid_amount_precision", $"{from} amounts carry at most {fromDecimals} decimal places.", "amount");

        var fee = decimal.Round(amount * spreadBasisPoints / 10_000m, fromDecimals, MidpointRounding.ToPositiveInfinity);
        var amountOut = decimal.Round((amount - fee) * toPerFrom, toDecimals, MidpointRounding.ToZero);
        if (amountOut <= 0)
            throw PaymentsException.Unprocessable("amount_below_minimum", "This amount is too small to convert.", "amount");
        return new CoinQuote(from, to, amount, fee, amountOut, decimal.Round(amountOut / amount, 12, MidpointRounding.ToZero));
    }

    static PaymentsException Unpriced(string why) =>
        new(503, new ApiError("provider_unavailable", $"NdeipiCoin cannot be quoted right now. {why}"));
}
