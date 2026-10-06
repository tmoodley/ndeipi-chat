using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Treasury;

namespace Ndeipi.Payments.Tests;

/// <summary>Low monitoring thresholds, so review is easy to reach: 100 points at once, 150 a day.</summary>
public sealed class MonitoredApp : PaymentsApp
{
    protected override IDictionary<string, string?> Overrides => new Dictionary<string, string?>
    {
        ["Payments:Monitoring:ReviewSingleFrom"] = "100",
        ["Payments:Monitoring:ReviewDailyFrom"] = "150"
    };
}

/// <summary>
/// Transaction monitoring (SRV-KYC-04) and the operator API (SRV-OPS-04): what is held, how
/// operators release or reject it, four-eyes manual refunds, and the audit trail behind them.
/// </summary>
public sealed class OperationsTests(MonitoredApp app) : IClassFixture<MonitoredApp>
{
    static async Task<JsonElement> JsonAsync(Task<HttpResponseMessage> request) => await (await request).JsonAsync();

    static Task<HttpResponseMessage> PostAsync(HttpClient client, string path, object? body = null) =>
        client.PostAsJsonAsync(path, body ?? new { });

    [Fact]
    public async Task A_large_transfer_waits_in_the_senders_pending_balance_until_an_operator_releases_it()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 200m, earned: 20m);
        var ops = await app.OperatorAsync("Nomsa (compliance)");

        var held = await (await acme.SendAsync(aliceWallet, bobWallet, "120.00")).JsonAsync();
        var id = held.GetProperty("id").GetString()!;
        var queue = await JsonAsync(ops.GetAsync("/ops/v1/reviews"));
        var released = await JsonAsync(PostAsync(ops, $"/ops/v1/transfers/{id}/release"));

        Assert.Equal("in_review", held.GetProperty("state").GetString());
        Assert.Equal("large_amount", held.GetProperty("state_reason").GetProperty("code").GetString());
        Assert.Contains(queue.GetProperty("data").EnumerateArray(), r => r.GetProperty("transfer_id").GetString() == id);
        Assert.Equal("completed", released.GetProperty("state").GetString());
        var bobs = await acme.BalanceAsync(bob, bobWallet);
        Assert.Equal("120.00", bobs.GetProperty("available").GetString());
        Assert.Equal("100.00", bobs.GetProperty("cashable").GetString()); // the 20 earned points stayed earned
        var alices = await acme.BalanceAsync(alice, aliceWallet);
        Assert.Equal("100.00", alices.GetProperty("available").GetString());
        Assert.Equal("0.00", alices.GetProperty("pending").GetString());

        var audit = await app.DbAsync(db => db.AuditLog.Where(a => a.Path == $"/ops/v1/transfers/{id}/release").SingleAsync());
        Assert.Equal("Nomsa (compliance)", audit.Operator);
        Assert.Equal(acme.Id, audit.IntegratorId);
    }

    [Fact]
    public async Task A_rejected_transfer_returns_each_kind_of_point_to_the_sender()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 100m, earned: 30m);
        var ops = await app.OperatorAsync("Thabo");
        var id = (await (await acme.SendAsync(aliceWallet, bobWallet, "110.00")).JsonAsync()).GetProperty("id").GetString();

        var noReason = await PostAsync(ops, $"/ops/v1/transfers/{id}/reject", new { });
        var rejected = await JsonAsync(PostAsync(ops, $"/ops/v1/transfers/{id}/reject", new { reason = "Unusual for this account." }));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, noReason.StatusCode);
        Assert.Equal("failed", rejected.GetProperty("state").GetString());
        Assert.Equal("Unusual for this account.", rejected.GetProperty("state_reason").GetProperty("message").GetString());
        var alices = await acme.BalanceAsync(alice, aliceWallet);
        Assert.Equal("130.00", alices.GetProperty("available").GetString());
        Assert.Equal("100.00", alices.GetProperty("cashable").GetString());
        Assert.Equal("0.00", (await acme.BalanceAsync(bob, bobWallet)).GetProperty("available").GetString());
    }

    [Fact]
    public async Task Small_transfers_add_up_to_review_over_a_day()
    {
        var acme = await app.CreateIntegratorAsync();
        var (_, aliceWallet) = await acme.HolderAsync();
        var (_, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 300m);

        var states = new List<string>();
        for (var i = 0; i < 3; i++)
            states.Add((await (await acme.SendAsync(aliceWallet, bobWallet, "60.00")).JsonAsync()).GetProperty("state").GetString()!);

        Assert.Equal(new[] { "completed", "completed", "in_review" }, states);
    }

    [Fact]
    public async Task A_held_cash_out_is_not_paid_until_released_and_is_refunded_if_rejected()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = await acme.DepositAccountAsync(user, wallet), amount = "500.00" });
        var payout = (await (await acme.PostAsync($"/v1/users/{user}/payout_accounts", new
        {
            type = "bank", currency = "zar", country = "ZA", rail = "absa_eft", account_owner_name = "Alice Moyo",
            bank = new { bank_name = "Absa", account_number = "62012345678" }
        })).JsonAsync()).GetProperty("id").GetString()!;
        var ops = await app.OperatorAsync("Lindiwe");

        var released = (await (await acme.CashOutAsync(wallet, payout, "120.00")).JsonAsync()).GetProperty("id").GetString();
        await Task.Delay(300); // the ramp worker must leave it alone
        Assert.Equal("in_review", (await (await acme.GetAsync($"/v1/transfers/{released}")).JsonAsync()).GetProperty("state").GetString());
        var submitted = await JsonAsync(PostAsync(ops, $"/ops/v1/transfers/{released}/release"));

        var rejectedId = (await (await acme.CashOutAsync(wallet, payout, "130.00")).JsonAsync()).GetProperty("id").GetString();
        var rejected = await JsonAsync(PostAsync(ops, $"/ops/v1/transfers/{rejectedId}/reject", new { reason = "Beneficiary check failed." }));

        Assert.Equal("payout_submitted", submitted.GetProperty("state").GetString());
        Assert.Equal("refunded", rejected.GetProperty("state").GetString());
        var balance = await acme.BalanceAsync(user, wallet);
        Assert.Equal("380.00", balance.GetProperty("cashable").GetString());
        Assert.Equal("120.00", balance.GetProperty("pending").GetString());
    }

    [Fact]
    public async Task A_deposit_held_for_review_is_credited_on_release_or_sent_back_on_rejection()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        var account = await acme.DepositAccountAsync(user, wallet);
        var ops = await app.OperatorAsync("Sipho");

        var kept = (await (await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = account, amount = "75.00", outcome = "in_review" })).JsonAsync()).GetProperty("id").GetString();
        var sentBack = (await (await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = account, amount = "40.00", outcome = "in_review" })).JsonAsync()).GetProperty("id").GetString();
        var released = await JsonAsync(PostAsync(ops, $"/ops/v1/transfers/{kept}/release"));
        var returned = await JsonAsync(PostAsync(ops, $"/ops/v1/transfers/{sentBack}/reject", new { reason = "Source of funds not shown." }));

        Assert.Equal("completed", released.GetProperty("state").GetString());
        Assert.Equal("75.00", released.GetProperty("receipt").GetProperty("amount_credited").GetString());
        Assert.Equal("returned", returned.GetProperty("state").GetString());
        Assert.Equal("75.00", (await acme.BalanceAsync(user, wallet)).GetProperty("cashable").GetString());
    }

    [Fact]
    public async Task The_operator_api_takes_operator_keys_only()
    {
        var acme = await app.CreateIntegratorAsync();
        var ops = await app.OperatorAsync("Zanele");
        var integratorOnOps = app.CreateClient();
        integratorOnOps.DefaultRequestHeaders.Add("Api-Key", acme.Key);

        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateClient().GetAsync("/ops/v1/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await integratorOnOps.GetAsync("/ops/v1/reviews")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ops.GetAsync("/v1/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await ops.GetAsync("/ops/v1/reviews")).StatusCode);
    }

    [Fact]
    public async Task A_manual_refund_needs_a_second_operator_and_is_paid_by_the_treasury()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = await acme.DepositAccountAsync(user, wallet), amount = "90.00" });
        var payout = (await (await acme.PostAsync($"/v1/users/{user}/payout_accounts", new
        {
            type = "bank", currency = "zar", country = "ZA", rail = "absa_eft", account_owner_name = "Alice Moyo",
            bank = new { bank_name = "Absa", account_number = "62012345678" }
        })).JsonAsync()).GetProperty("id").GetString()!;
        var cashOut = (await (await acme.CashOutAsync(wallet, payout, "80.00")).JsonAsync()).GetProperty("id").GetString()!;
        await acme.PostAsync($"/v1/sandbox/transfers/{cashOut}/payout_outcome", new { outcome = "completed" });
        await app.AsHouseAsync(async sp =>
        {
            await sp.GetRequiredService<TreasuryService>().DepositAsync("simulated_fiat", "zar", 1_000m, "Test capital", default);
            return 0;
        });
        var requester = await app.OperatorAsync("Ayanda");
        var approver = await app.OperatorAsync("Kagiso");

        var tooMuch = await PostAsync(requester, "/ops/v1/refunds", new { transfer_id = cashOut, amount = "80.01", reason = "Payout lost." });
        var refund = await JsonAsync(PostAsync(requester, "/ops/v1/refunds", new { transfer_id = cashOut, amount = "80.00", reason = "Payout lost by the bank." }));
        var refundId = refund.GetProperty("id").GetString();
        var selfApproved = await PostAsync(requester, $"/ops/v1/refunds/{refundId}/approve");
        var approved = await JsonAsync(PostAsync(approver, $"/ops/v1/refunds/{refundId}/approve"));
        var again = await PostAsync(requester, "/ops/v1/refunds", new { transfer_id = cashOut, amount = "1.00", reason = "Twice." });

        Assert.Equal("amount_above_maximum", await tooMuch.ErrorCodeAsync());
        Assert.Equal("requested", refund.GetProperty("status").GetString());
        Assert.Equal("four_eyes", await selfApproved.ErrorCodeAsync());
        Assert.Equal("approved", approved.GetProperty("status").GetString());
        Assert.Equal("Kagiso", approved.GetProperty("decided_by").GetString());
        Assert.Equal("90.00", (await acme.BalanceAsync(user, wallet)).GetProperty("cashable").GetString());
        Assert.Equal("amount_above_maximum", await again.ErrorCodeAsync());
    }
}
