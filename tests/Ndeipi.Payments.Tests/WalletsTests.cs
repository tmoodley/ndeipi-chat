using System.Net;
using System.Text.Json;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Tests.Infrastructure;

namespace Ndeipi.Payments.Tests;

/// <summary>Wallets (SRS §4.2): creation rules, balances by bucket, and history that sums to the balance.</summary>
public sealed class WalletsTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    [Fact]
    public async Task Only_an_approved_user_gets_a_wallet()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await acme.UserAsync(approved: false);

        var response = await acme.PostAsync($"/v1/users/{user}/wallets", new { asset = Accounts.Points });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("user_not_approved", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_points_wallet_opens_empty_at_two_decimal_places()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await acme.UserAsync();

        var response = await acme.PostAsync($"/v1/users/{user}/wallets", new { asset = Accounts.Points, metadata = new Dictionary<string, string> { ["label"] = "main" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var wallet = await response.JsonAsync();
        Assert.StartsWith("wal_", wallet.GetProperty("id").GetString());
        Assert.Equal("wallet", wallet.GetProperty("object").GetString());
        Assert.Equal(user, wallet.GetProperty("user_id").GetString());
        Assert.Equal(2, wallet.GetProperty("decimals").GetInt32());
        Assert.Equal("active", wallet.GetProperty("status").GetString());
        Assert.Equal("0.00", wallet.GetProperty("balance").GetProperty("available").GetString());
        Assert.Equal("0.00", wallet.GetProperty("balance").GetProperty("cashable").GetString());
        Assert.Equal("main", wallet.GetProperty("metadata").GetProperty("label").GetString());
    }

    [Fact]
    public async Task One_wallet_per_asset()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();

        var response = await acme.PostAsync($"/v1/users/{user}/wallets", new { asset = Accounts.Points });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.JsonAsync();
        Assert.Equal("wallet_exists", error.GetProperty("code").GetString());
        Assert.Equal(wallet, error.GetProperty("existing_id").GetString());
    }

    [Fact]
    public async Task A_ndeipicoin_wallet_needs_the_price_risk_acknowledged()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await acme.UserAsync();

        var refused = await acme.PostAsync($"/v1/users/{user}/wallets", new { asset = Accounts.Coin });
        var created = await acme.PostAsync($"/v1/users/{user}/wallets", new { asset = Accounts.Coin, acknowledge_price_risk = true });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, refused.StatusCode);
        Assert.Equal("price_risk_not_acknowledged", await refused.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var wallet = await created.JsonAsync();
        Assert.Equal(8, wallet.GetProperty("decimals").GetInt32());
        Assert.Equal("0.00000000", wallet.GetProperty("balance").GetProperty("cashable").GetString());
        Assert.True(wallet.GetProperty("price_risk_acknowledged_at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task Unknown_assets_are_refused()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await acme.UserAsync();

        var response = await acme.PostAsync($"/v1/users/{user}/wallets", new { asset = "usd-stable" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unsupported_asset", (await response.JsonAsync()).GetProperty("details")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Available_counts_purchased_and_earned_points_but_only_purchased_are_cashable()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();

        await acme.FundAsync(wallet, purchased: 100.00m, earned: 15.50m);
        var balance = await acme.BalanceAsync(user, wallet);

        Assert.Equal("wallet_balance", balance.GetProperty("object").GetString());
        Assert.Equal("115.50", balance.GetProperty("available").GetString());
        Assert.Equal("100.00", balance.GetProperty("cashable").GetString());
        Assert.Equal("0.00", balance.GetProperty("pending").GetString());
    }

    [Fact]
    public async Task History_lists_every_movement_and_sums_to_the_balance_per_bucket()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        var (_, other) = await acme.HolderAsync();
        await acme.FundAsync(wallet, purchased: 40m, earned: 5m);
        await acme.SendAsync(wallet, other, "12.25");

        var history = await (await acme.GetAsync($"/v1/users/{user}/wallets/{wallet}/history?limit=100")).JsonAsync();
        var entries = history.GetProperty("data").EnumerateArray().ToList();

        Assert.All(entries, e => Assert.StartsWith("whe_", e.GetProperty("id").GetString()));
        decimal Sum(string bucket) => entries.Where(e => e.GetProperty("bucket").GetString() == bucket).Sum(e => decimal.Parse(e.GetProperty("amount").GetString()!));
        Assert.Equal(32.75m, Sum("available") + Sum("earned"));
        Assert.Equal(0m, Sum("earned"));
        Assert.Equal(32.75m, Sum("available"));
        var balance = await acme.BalanceAsync(user, wallet);
        Assert.Equal("32.75", balance.GetProperty("available").GetString());
        Assert.Contains(entries, e => e.GetProperty("transfer_id").ValueKind == JsonValueKind.String);
        Assert.Contains(entries, e => e.GetProperty("transfer_id").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task History_pages_newest_first_by_cursor()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        for (var i = 1; i <= 5; i++)
            await acme.FundAsync(wallet, purchased: i);

        var first = await (await acme.GetAsync($"/v1/users/{user}/wallets/{wallet}/history?limit=2")).JsonAsync();
        var lastId = first.GetProperty("data")[1].GetProperty("id").GetString();
        var second = await (await acme.GetAsync($"/v1/users/{user}/wallets/{wallet}/history?limit=2&starting_after={lastId}")).JsonAsync();
        var back = await (await acme.GetAsync($"/v1/users/{user}/wallets/{wallet}/history?limit=2&ending_before={second.GetProperty("data")[0].GetProperty("id").GetString()}")).JsonAsync();

        static IEnumerable<string> Amounts(JsonElement page) => page.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("amount").GetString()!);
        Assert.Equal(new[] { "5.00", "4.00" }, Amounts(first));
        Assert.True(first.GetProperty("has_more").GetBoolean());
        Assert.Equal(new[] { "3.00", "2.00" }, Amounts(second));
        Assert.Equal(new[] { "5.00", "4.00" }, Amounts(back));
        Assert.Equal(HttpStatusCode.BadRequest, (await acme.GetAsync($"/v1/users/{user}/wallets/{wallet}/history?starting_after=wal_nope")).StatusCode);
    }

    [Fact]
    public async Task Another_integrator_cannot_see_the_wallet()
    {
        var acme = await app.CreateIntegratorAsync("Acme");
        var zed = await app.CreateIntegratorAsync("Zed");
        var (user, wallet) = await acme.HolderAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await zed.GetAsync($"/v1/users/{user}/wallets/{wallet}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await zed.GetAsync($"/v1/users/{user}/wallets")).StatusCode);
    }

    [Fact]
    public void History_ids_round_trip_and_reject_anything_else()
    {
        foreach (var number in new[] { 1L, 31L, 32L, 123_456_789L, long.MaxValue })
            Assert.Equal(number, Ids.ToNumber(Ids.WalletHistoryEntry, Ids.FromNumber(Ids.WalletHistoryEntry, number)));
        Assert.Null(Ids.ToNumber(Ids.WalletHistoryEntry, "whe_ZZZZZZZZZZZZZZZZZZZZZZZZZZ"));
        Assert.Null(Ids.ToNumber(Ids.WalletHistoryEntry, "wal_00000000000000000000000001"));
    }
}
