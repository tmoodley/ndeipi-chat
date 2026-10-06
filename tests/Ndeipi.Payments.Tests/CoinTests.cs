using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Treasury;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// NdeipiCoin, the opt-in asset: trades with the manual OTC desk set its price, and conversions
/// to and from points are priced from it with Ndeipi's spread (FR-RATE-04, FR-XFER-10).
/// </summary>
public sealed class CoinTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    const string Points = "ndeipi-points";
    const string Coin = "ndeipi-coin";

    Task<T> InScopeAsync<T>(Func<IServiceProvider, Task<T>> work) => app.AsIntegratorAsync(Guid.NewGuid(), work);

    static OtcTradeEntry Trade(decimal coin, decimal usd, DateTimeOffset at, string? desk = null) =>
        new(OtcSide.Buy, coin, usd, "ABSA-" + Guid.NewGuid().ToString("N")[..8], desk ?? "DESK-" + Guid.NewGuid().ToString("N")[..8], "treasury@ndeipi.test", at);

    [Fact]
    public void Buying_charges_the_spread_in_points_and_rounds_the_coin_down()
    {
        // 1,000 points at 0.5 coin per point with a 1.5% spread: 15 points fee, 492.5 coin.
        var quote = CoinPricing.Price(Points, Coin, 1000m, 2, 8, 0.5m, 150);

        Assert.Equal(15.00m, quote.Fee);
        Assert.Equal(492.5m, quote.AmountOut);
        Assert.Equal(0.4925m, quote.Rate);
    }

    [Theory]
    [InlineData(0.01, 7)]
    [InlineData(1.00, 150)]
    [InlineData(12.34, 150)]
    [InlineData(999.99, 300)]
    public void Converting_there_and_back_never_returns_more_points(decimal points, int spread)
    {
        foreach (var pointsPerCoin in new[] { 0.0137m, 1m, 18.25m, 4200m })
        {
            try
            {
                var bought = CoinPricing.Price(Points, Coin, points, 2, 8, 1 / pointsPerCoin, spread);
                var sold = CoinPricing.Price(Coin, Points, bought.AmountOut, 8, 2, pointsPerCoin, spread);
                Assert.True(sold.AmountOut <= points, $"{points} points came back as {sold.AmountOut} at {pointsPerCoin} points per coin");
            }
            catch (PaymentsException e) when (e.Error.Code == "amount_below_minimum")
            {
                // Too small to convert at this price; refused rather than rounded to nothing.
            }
        }
    }

    [Fact]
    public void Amounts_finer_than_the_asset_allows_are_refused()
    {
        var thrown = Assert.Throws<PaymentsException>(() => CoinPricing.Price(Points, Coin, 10.001m, 2, 8, 1m, 150));
        Assert.Equal("invalid_amount_precision", thrown.Error.Code);
    }

    [Fact]
    public async Task The_latest_otc_trade_sets_the_price_through_the_points_usd_price()
    {
        var now = DateTimeOffset.UtcNow;
        await InScopeAsync(async sp =>
        {
            var desk = sp.GetRequiredService<OtcDesk>();
            await desk.RecordAsync(Trade(coin: 1000m, usd: 50m, now.AddHours(-2)), default);
            return await desk.RecordAsync(Trade(coin: 1000m, usd: 110m, now.AddMinutes(-5)), default);
        });

        // 0.11 USD per coin over the sandbox's sample 0.055 USD per point: 2 points per coin.
        var pointsPerCoin = await InScopeAsync(sp => sp.GetRequiredService<CoinPricing>().PointsPerCoinAsync(default));
        var quote = await InScopeAsync(sp => sp.GetRequiredService<CoinPricing>().QuoteAsync(Coin, 100m, default));

        Assert.Equal(2m, pointsPerCoin);
        Assert.Equal(Points, quote.To);
        Assert.Equal(1.5m, quote.Fee);           // 1.5% of 100 coin
        Assert.Equal(197.00m, quote.AmountOut);  // 98.5 coin × 2
    }

    [Fact]
    public async Task A_desk_trade_is_recorded_once()
    {
        var entry = Trade(coin: 10m, usd: 1m, DateTimeOffset.UtcNow.AddMinutes(-1), desk: "DESK-ONCE-" + Guid.NewGuid().ToString("N")[..6]);

        await InScopeAsync(sp => sp.GetRequiredService<OtcDesk>().RecordAsync(entry, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(sp => sp.GetRequiredService<OtcDesk>().RecordAsync(entry, default)));
    }

    [Fact]
    public async Task Otc_trades_are_append_only()
    {
        var trade = await InScopeAsync(sp => sp.GetRequiredService<OtcDesk>().RecordAsync(Trade(5m, 1m, DateTimeOffset.UtcNow.AddMinutes(-1)), default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => InScopeAsync(async sp =>
        {
            var db = sp.GetRequiredService<PaymentsDbContext>();
            var stored = await db.OtcTrades.FindAsync(trade.Id);
            stored!.UsdAmount = 2m;
            return await db.SaveChangesAsync();
        }));
    }

    [Fact]
    public void A_conversion_completes_or_fails_and_nothing_else()
    {
        Assert.Equal(TransferState.Pending, TransferStates.Initial(TransferKind.Conversion));
        Assert.True(TransferStates.CanMove(TransferKind.Conversion, TransferState.Pending, TransferState.Completed));
        Assert.True(TransferStates.CanMove(TransferKind.Conversion, TransferState.Pending, TransferState.Failed));
        Assert.False(TransferStates.Uses(TransferKind.Conversion, TransferState.InReview));
        Assert.True(TransferStates.IsFinal(TransferKind.Conversion, TransferState.Completed));
    }
}

/// <summary>Quoting stops when the price is unknown, rather than quoting from a stale trade.</summary>
public sealed class StaleCoinPriceTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    [Fact]
    public async Task No_trade_or_an_old_trade_means_no_quote()
    {
        var noTrade = await Assert.ThrowsAsync<PaymentsException>(() =>
            app.AsIntegratorAsync(Guid.NewGuid(), sp => sp.GetRequiredService<CoinPricing>().PointsPerCoinAsync(default)));
        Assert.Equal(503, noTrade.Status);
        Assert.Equal("provider_unavailable", noTrade.Error.Code);

        await app.AsIntegratorAsync(Guid.NewGuid(), sp => sp.GetRequiredService<OtcDesk>().RecordAsync(
            new(OtcSide.Buy, 100m, 10m, "ABSA-OLD", "DESK-OLD", "treasury@ndeipi.test", DateTimeOffset.UtcNow.AddDays(-4)), default));

        var stale = await Assert.ThrowsAsync<PaymentsException>(() =>
            app.AsIntegratorAsync(Guid.NewGuid(), sp => sp.GetRequiredService<CoinPricing>().PointsPerCoinAsync(default)));
        Assert.Contains("too old", stale.Error.Message);
    }
}
