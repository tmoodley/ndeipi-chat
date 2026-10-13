using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Treasury;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// NdeipiCoin conversions at locked quotes (FR-RATE-04, FR-XFER-10), settled from Ndeipi's own
/// stock and treasury, with only cashable points able to buy NdeipiCoin.
/// </summary>
public sealed class ConversionTests(PaymentsApp app) : IClassFixture<PaymentsApp>, IAsyncLifetime
{
    /// <summary>
    /// The treasury's setup, once per class: dollars deposited, 100,000 NdeipiCoin bought from the
    /// desk at 0.11 USD (2 points per coin at 0.055 USD a point), and points bought for paying sellers.
    /// </summary>
    public async Task InitializeAsync() => await app.AsHouseAsync(async sp =>
    {
        if (await sp.GetRequiredService<PaymentsDbContext>().OtcTrades.AnyAsync())
            return 0;
        var treasury = sp.GetRequiredService<TreasuryService>();
        await treasury.DepositAsync("simulated_fiat", "usd", 20_000m, "Test capital", default);
        await sp.GetRequiredService<OtcDesk>().RecordAsync(
            new(OtcSide.Buy, 100_000m, 11_000m, "ABSA-1", "DESK-1", "treasury@ndeipi.test", DateTimeOffset.UtcNow.AddMinutes(-1)), default);
        await treasury.BuyPointsAsync("usd", 1_000m, default);
        return 0;
    });

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>An approved user with both wallets: (user, points wallet, coin wallet).</summary>
    async Task<(string User, string Points, string Coin)> CustomerAsync(TestIntegrator integrator)
    {
        var (user, points) = await integrator.HolderAsync();
        return (user, points, await integrator.WalletAsync(user, Accounts.Coin));
    }

    static Task<HttpResponseMessage> QuoteAsync(TestIntegrator integrator, string from, string to, string amount) =>
        integrator.PostAsync("/v1/quotes", new { source_wallet_id = from, destination_wallet_id = to, amount });

    static Task<HttpResponseMessage> ConvertAsync(TestIntegrator integrator, string from, string to, string amount, string? quoteId) =>
        integrator.PostAsync("/v1/transfers", new
        {
            source = new { type = "wallet", wallet_id = from },
            destination = new { type = "wallet", wallet_id = to },
            amount,
            quote_id = quoteId
        });

    [Fact]
    public async Task Buying_ndeipicoin_at_a_quote_charges_the_spread_and_settles_at_once()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, points, coin) = await CustomerAsync(acme);
        await acme.FundAsync(points, purchased: 100m);

        var quote = await (await QuoteAsync(acme, points, coin, "100.00")).JsonAsync();
        var created = await ConvertAsync(acme, points, coin, "100.00", quote.GetProperty("id").GetString());
        var transfer = await created.JsonAsync();
        var reused = await ConvertAsync(acme, points, coin, "100.00", quote.GetProperty("id").GetString());

        Assert.Equal("quote", quote.GetProperty("object").GetString());
        Assert.Equal("open", quote.GetProperty("status").GetString());
        Assert.Equal("1.50", quote.GetProperty("fees")[0].GetProperty("amount").GetString());
        Assert.Equal("49.25000000", quote.GetProperty("amount_out").GetString()); // 98.50 points at 2 a coin
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("conversion", transfer.GetProperty("kind").GetString());
        Assert.Equal("completed", transfer.GetProperty("state").GetString());
        Assert.Equal("49.25000000", transfer.GetProperty("receipt").GetProperty("amount_credited").GetString());
        Assert.Equal("0.00", (await acme.BalanceAsync(user, points)).GetProperty("available").GetString());
        Assert.Equal("49.25000000", (await acme.BalanceAsync(user, coin)).GetProperty("available").GetString());
        Assert.Equal("quote_used", await reused.ErrorCodeAsync());
        Assert.Equal("used", (await (await acme.GetAsync($"/v1/quotes/{quote.GetProperty("id").GetString()}")).JsonAsync()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Selling_ndeipicoin_back_pays_cashable_points_from_the_treasury()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, points, coin) = await CustomerAsync(acme);
        await acme.FundAsync(points, purchased: 200m);
        var buy = await (await QuoteAsync(acme, points, coin, "200.00")).JsonAsync();
        await ConvertAsync(acme, points, coin, "200.00", buy.GetProperty("id").GetString());

        var sell = await (await QuoteAsync(acme, coin, points, "50.00000000")).JsonAsync();
        var sold = await (await ConvertAsync(acme, coin, points, "50.00000000", sell.GetProperty("id").GetString())).JsonAsync();

        Assert.Equal("completed", sold.GetProperty("state").GetString());
        Assert.Equal("98.50", sell.GetProperty("amount_out").GetString()); // (50 − 0.75) coin × 2
        var pointsBalance = await acme.BalanceAsync(user, points);
        Assert.Equal("98.50", pointsBalance.GetProperty("cashable").GetString());
        Assert.Equal("48.50000000", (await acme.BalanceAsync(user, coin)).GetProperty("available").GetString());
    }

    [Fact]
    public async Task Earned_points_cannot_buy_ndeipicoin()
    {
        var acme = await app.CreateIntegratorAsync();
        var (_, points, coin) = await CustomerAsync(acme);
        await acme.FundAsync(points, purchased: 10m, earned: 90m);

        var response = await QuoteAsync(acme, points, coin, "50.00");

        Assert.Equal("amount_exceeds_cashable", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_conversion_needs_a_matching_unexpired_quote()
    {
        var acme = await app.CreateIntegratorAsync();
        var (_, points, coin) = await CustomerAsync(acme);
        await acme.FundAsync(points, purchased: 100m);
        var quoteId = (await (await QuoteAsync(acme, points, coin, "40.00")).JsonAsync()).GetProperty("id").GetString()!;

        var missing = await ConvertAsync(acme, points, coin, "40.00", null);
        var mismatch = await ConvertAsync(acme, points, coin, "41.00", quoteId);
        await app.DbAsync(db => db.Quotes.IgnoreQueryFilters().Where(q => q.Id == quoteId)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        var expired = await ConvertAsync(acme, points, coin, "40.00", quoteId);

        Assert.Equal("quote_required", await missing.ErrorCodeAsync());
        Assert.Equal("quote_mismatch", await mismatch.ErrorCodeAsync());
        Assert.Equal("quote_expired", await expired.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_quote_larger_than_ndeipis_stock_is_refused()
    {
        var acme = await app.CreateIntegratorAsync();
        var (_, points, coin) = await CustomerAsync(acme);
        await acme.FundAsync(points, purchased: 400_000m);

        var response = await QuoteAsync(acme, points, coin, "400000.00"); // ≈197,000 coin against 100,000 in stock

        Assert.Equal("insufficient_liquidity", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task The_indicative_rate_comes_from_the_last_otc_trade()
    {
        var acme = await app.CreateIntegratorAsync();

        var rate = await (await acme.GetAsync("/v1/rates?from=ndeipi-coin&to=ndeipi-points")).JsonAsync();

        Assert.Equal("2", rate.GetProperty("rate").GetString());
        Assert.Equal("indicative", rate.GetProperty("type").GetString());
    }
}
