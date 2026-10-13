using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// On-ramp and off-ramp on the sandbox rails (SRS §4.4 to §4.6): routes and fixed rates, standing
/// and one-off deposits, payout accounts, and payouts that complete or are refunded, with the points
/// reserve always covering what can be cashed out.
/// </summary>
public sealed class RampsTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    static object BankAccount(string number = "62012345678") => new
    {
        type = "bank", currency = "zar", country = "ZA", rail = "absa_eft", account_owner_name = "Alice Moyo",
        bank = new { bank_name = "Absa", account_number = number, routing_code = "632005", account_type = "savings" }
    };

    Task<decimal> HouseBalanceAsync(LedgerAccountKind kind, string asset) =>
        app.DbAsync(db => db.LedgerAccounts.IgnoreQueryFilters()
            .Where(a => a.IntegratorId == IntegratorScope.House && a.Kind == kind && a.Asset == asset && a.Provider == null)
            .SumAsync(a => a.Balance));

    async Task<JsonElement> WaitForStateAsync(TestIntegrator integrator, string transferId, string state)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var transfer = await (await integrator.GetAsync($"/v1/transfers/{transferId}")).JsonAsync();
            if (transfer.GetProperty("state").GetString() == state)
                return transfer;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Transfer stayed {transfer.GetProperty("state").GetString()}.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Routes_list_each_rail_in_its_currencies_plus_transfers_and_conversions()
    {
        var acme = await app.CreateIntegratorAsync();

        var routes = (await (await acme.GetAsync("/v1/routes")).JsonAsync()).GetProperty("data").EnumerateArray().ToList();
        var onramps = (await (await acme.GetAsync("/v1/routes?kind=onramp")).JsonAsync()).GetProperty("data").EnumerateArray().ToList();

        Assert.Contains(routes, r => r.GetProperty("kind").GetString() == "conversion");
        Assert.Contains(routes, r => r.GetProperty("kind").GetString() == "user_to_user");
        Assert.Equal(new[] { ("absa_eft", "zar"), ("paypal", "usd") },
            onramps.Select(r => (r.GetProperty("source").GetProperty("rail").GetString()!, r.GetProperty("source").GetProperty("currency").GetString()!)).Order());
        var absa = onramps.Single(r => r.GetProperty("source").GetProperty("rail").GetString() == "absa_eft");
        Assert.Equal("10.00", absa.GetProperty("min_amount").GetString());
        Assert.Equal("ndeipi-points", absa.GetProperty("destination").GetProperty("asset").GetString());
    }

    [Fact]
    public async Task Rates_are_the_fixed_points_price_both_ways()
    {
        var acme = await app.CreateIntegratorAsync();

        var buying = await (await acme.GetAsync("/v1/rates?from=zar&to=ndeipi-points")).JsonAsync();
        var selling = await (await acme.GetAsync("/v1/rates?from=ndeipi-points&to=usd")).JsonAsync();
        var unknown = await acme.GetAsync("/v1/rates?from=eur&to=ndeipi-points");

        Assert.Equal("1", buying.GetProperty("rate").GetString());
        Assert.Equal("fixed", buying.GetProperty("type").GetString());
        Assert.Equal("0.055", selling.GetProperty("rate").GetString());
        Assert.Equal("unsupported_route", await unknown.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_standing_deposit_account_gives_bank_details_on_absa_but_not_on_paypal()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();

        var created = await acme.PostAsync($"/v1/users/{user}/deposit_accounts", new { source = new { currency = "zar", rail = "absa_eft" }, destination = new { wallet_id = wallet } });
        var paypal = await acme.PostAsync($"/v1/users/{user}/deposit_accounts", new { source = new { currency = "usd", rail = "paypal" }, destination = new { wallet_id = wallet } });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var account = await created.JsonAsync();
        Assert.StartsWith("da_", account.GetProperty("id").GetString());
        var instructions = account.GetProperty("deposit_instructions");
        Assert.Equal("bank", instructions.GetProperty("type").GetString());
        Assert.Equal("absa_eft", instructions.GetProperty("rail").GetString());
        Assert.False(string.IsNullOrEmpty(instructions.GetProperty("account_number").GetString()));
        Assert.False(string.IsNullOrEmpty(instructions.GetProperty("reference").GetString()));
        Assert.Equal("unsupported_route", await paypal.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_deposit_is_credited_as_cashable_points_and_backed_in_the_reserve()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        var accountId = await acme.DepositAccountAsync(user, wallet);
        var reserveBefore = await HouseBalanceAsync(LedgerAccountKind.PointsReserve, "zar");

        var transfer = await (await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = accountId, amount = "100.00" })).JsonAsync();

        Assert.Equal("onramp", transfer.GetProperty("kind").GetString());
        Assert.Equal("completed", transfer.GetProperty("state").GetString());
        Assert.Equal(accountId, transfer.GetProperty("deposit_account_id").GetString());
        Assert.Equal("100.00", transfer.GetProperty("receipt").GetProperty("amount_received").GetString());
        Assert.Equal("100.00", transfer.GetProperty("receipt").GetProperty("amount_credited").GetString());
        Assert.StartsWith("SIM-", transfer.GetProperty("receipt").GetProperty("rail_reference").GetString());
        Assert.Equal("100.00", (await acme.BalanceAsync(user, wallet)).GetProperty("cashable").GetString());
        Assert.Equal(reserveBefore + 100m, await HouseBalanceAsync(LedgerAccountKind.PointsReserve, "zar"));

        var activity = await (await acme.GetAsync($"/v1/users/{user}/deposit_accounts/{accountId}/activity")).JsonAsync();
        Assert.Equal("100.00", activity.GetProperty("data")[0].GetProperty("amount_credited").GetString());
        var events = await (await acme.GetAsync($"/v1/events?object_id={transfer.GetProperty("id").GetString()}&limit=100")).JsonAsync();
        Assert.Contains(events.GetProperty("data").EnumerateArray(), e => e.GetProperty("type").GetString() == EventTypes.DepositReceived);
    }

    [Fact]
    public async Task A_deposit_below_the_minimum_or_to_a_deactivated_account_is_returned()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        var accountId = await acme.DepositAccountAsync(user, wallet);

        var small = await (await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = accountId, amount = "5.00" })).JsonAsync();
        await acme.PostAsync($"/v1/users/{user}/deposit_accounts/{accountId}/deactivate", null);
        var afterDeactivation = await (await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = accountId, amount = "50.00" })).JsonAsync();
        var reactivated = await (await acme.PostAsync($"/v1/users/{user}/deposit_accounts/{accountId}/reactivate", null)).JsonAsync();

        Assert.Equal("returned", small.GetProperty("state").GetString());
        Assert.Equal("amount_below_minimum", small.GetProperty("state_reason").GetProperty("code").GetString());
        Assert.Equal("returned", afterDeactivation.GetProperty("state").GetString());
        Assert.Equal("deposit_account_deactivated", afterDeactivation.GetProperty("state_reason").GetProperty("code").GetString());
        Assert.Equal("active", reactivated.GetProperty("status").GetString());
        Assert.Equal("0.00", (await acme.BalanceAsync(user, wallet)).GetProperty("available").GetString());
    }

    [Fact]
    public async Task A_one_off_paypal_on_ramp_waits_for_approval_then_credits_points()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();

        var created = await acme.PostAsync("/v1/transfers", new
        {
            source = new { type = "fiat", currency = "usd", rail = "paypal" },
            destination = new { type = "wallet", wallet_id = wallet },
            amount = "55.00"
        });
        var transfer = await created.JsonAsync();
        var id = transfer.GetProperty("id").GetString();
        var paid = await (await acme.PostAsync("/v1/sandbox/deposits", new { transfer_id = id, amount = "55.00" })).JsonAsync();

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("awaiting_funds", transfer.GetProperty("state").GetString());
        var instructions = transfer.GetProperty("deposit_instructions");
        Assert.Equal("paypal", instructions.GetProperty("type").GetString());
        Assert.StartsWith("https://", instructions.GetProperty("approval_url").GetString());
        Assert.Equal("55.00", instructions.GetProperty("amount").GetString());
        Assert.Equal("completed", paid.GetProperty("state").GetString());
        Assert.Equal("1000.00", paid.GetProperty("receipt").GetProperty("amount_credited").GetString()); // 55 / 0.055
        Assert.Equal("55.00", paid.GetProperty("receipt").GetProperty("amount_expected").GetString());
        Assert.Equal("1000.00", (await acme.BalanceAsync(user, wallet)).GetProperty("cashable").GetString());
    }

    [Fact]
    public async Task A_deposit_held_for_review_credits_nothing_yet()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        var accountId = await acme.DepositAccountAsync(user, wallet);

        var transfer = await (await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = accountId, amount = "300.00", outcome = "in_review" })).JsonAsync();

        Assert.Equal("in_review", transfer.GetProperty("state").GetString());
        Assert.Equal("0.00", (await acme.BalanceAsync(user, wallet)).GetProperty("available").GetString());
    }

    [Fact]
    public async Task An_on_ramp_nobody_paid_is_canceled_when_its_instructions_expire()
    {
        var acme = await app.CreateIntegratorAsync();
        var (_, wallet) = await acme.HolderAsync();
        var id = (await (await acme.PostAsync("/v1/transfers", new
        {
            source = new { type = "fiat", currency = "zar", rail = "absa_eft" },
            destination = new { type = "wallet", wallet_id = wallet },
            amount = "200.00"
        })).JsonAsync()).GetProperty("id").GetString()!;

        await app.DbAsync(db => db.Transfers.IgnoreQueryFilters().Where(t => t.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var canceled = await WaitForStateAsync(acme, id, "canceled");

        Assert.Equal("expired", canceled.GetProperty("state_reason").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Payout_accounts_are_validated_per_field_screened_and_returned_masked()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await acme.UserAsync();

        var bank = await acme.PostAsync($"/v1/users/{user}/payout_accounts", BankAccount());
        var badNumber = await acme.PostAsync($"/v1/users/{user}/payout_accounts", BankAccount("12AB"));
        var paypal = await acme.PostAsync($"/v1/users/{user}/payout_accounts", new
        {
            type = "paypal", currency = "usd", country = "ZA", rail = "paypal", account_owner_name = "Alice Moyo", paypal = new { email = "alice@example.com" }
        });
        var wrongType = await acme.PostAsync($"/v1/users/{user}/payout_accounts", new
        {
            type = "paypal", currency = "zar", country = "ZA", rail = "absa_eft", account_owner_name = "Alice Moyo", paypal = new { email = "alice@example.com" }
        });
        var sanctioned = await acme.PostAsync($"/v1/users/{user}/payout_accounts", new
        {
            type = "bank", currency = "zar", country = "ZA", rail = "absa_eft", account_owner_name = "Sanctioned Person",
            bank = new { bank_name = "Absa", account_number = "62012345678" }
        });

        Assert.Equal(HttpStatusCode.Created, bank.StatusCode);
        var bankAccount = await bank.JsonAsync();
        Assert.Equal("5678", bankAccount.GetProperty("bank").GetProperty("account_number_last4").GetString());
        Assert.DoesNotContain("62012345678", bankAccount.GetRawText());
        Assert.Equal("not_supported", bankAccount.GetProperty("name_check").GetString());
        Assert.Equal("bank.account_number", (await badNumber.JsonAsync()).GetProperty("field").GetString());
        Assert.Equal("a***e@example.com", (await paypal.JsonAsync()).GetProperty("paypal").GetProperty("email_masked").GetString());
        Assert.Equal("type", (await wrongType.JsonAsync()).GetProperty("field").GetString());
        Assert.Equal("sanctions_match", await sanctioned.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_cash_out_moves_points_to_pending_then_pays_out_from_the_reserve()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = await acme.DepositAccountAsync(user, wallet), amount = "100.00" });
        await acme.FundAsync(wallet, earned: 20m);
        var payout = (await (await acme.PostAsync($"/v1/users/{user}/payout_accounts", BankAccount())).JsonAsync()).GetProperty("id").GetString();

        var tooMuch = await acme.CashOutAsync(wallet, payout!, "120.00");
        var created = await acme.CashOutAsync(wallet, payout!, "50.00");
        var transfer = await created.JsonAsync();
        var id = transfer.GetProperty("id").GetString()!;
        var held = await acme.BalanceAsync(user, wallet);
        await WaitForStateAsync(acme, id, "payout_submitted");
        var reserveBefore = await HouseBalanceAsync(LedgerAccountKind.PointsReserve, "zar");
        var completed = await (await acme.PostAsync($"/v1/sandbox/transfers/{id}/payout_outcome", new { outcome = "completed" })).JsonAsync();

        Assert.Equal("amount_exceeds_cashable", await tooMuch.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("offramp", transfer.GetProperty("kind").GetString());
        Assert.Equal("pending", transfer.GetProperty("state").GetString());
        Assert.Equal("70.00", held.GetProperty("available").GetString());
        Assert.Equal("50.00", held.GetProperty("pending").GetString());
        Assert.True(completed.TryGetProperty("state", out _), completed.GetRawText());
        Assert.Equal("completed", completed.GetProperty("state").GetString());
        Assert.True(completed.TryGetProperty("receipt", out var r) && r.TryGetProperty("amount_paid_out", out _), completed.GetRawText());
        Assert.Equal("50.00", completed.GetProperty("receipt").GetProperty("amount_paid_out").GetString());
        Assert.Equal("0.00", (await acme.BalanceAsync(user, wallet)).GetProperty("pending").GetString());
        // The fiat left the reserve when the cash-out was accepted, not when it was paid.
        Assert.Equal(reserveBefore, await HouseBalanceAsync(LedgerAccountKind.PointsReserve, "zar"));
    }

    [Fact]
    public async Task A_returned_payout_refunds_the_points_and_tells_the_integrator()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = await acme.DepositAccountAsync(user, wallet), amount = "80.00" });
        var payout = (await (await acme.PostAsync($"/v1/users/{user}/payout_accounts", BankAccount())).JsonAsync()).GetProperty("id").GetString();
        var id = (await (await acme.CashOutAsync(wallet, payout!, "30.00")).JsonAsync()).GetProperty("id").GetString();

        var inUse = await acme.Client.DeleteAsync($"/v1/users/{user}/payout_accounts/{payout}");
        var refunded = await (await acme.PostAsync($"/v1/sandbox/transfers/{id}/payout_outcome",
            new { outcome = "returned", reason = new { code = "account_closed", message = "The account is closed." } })).JsonAsync();

        Assert.Equal("payout_account_in_use", await inUse.ErrorCodeAsync());
        Assert.True(refunded.GetProperty("state").GetString() == "refunded", refunded.GetRawText());
        Assert.Equal("payout_returned", refunded.GetProperty("state_reason").GetProperty("code").GetString());
        var balance = await acme.BalanceAsync(user, wallet);
        Assert.Equal("80.00", balance.GetProperty("cashable").GetString());
        Assert.Equal("0.00", balance.GetProperty("pending").GetString());
        var events = await (await acme.GetAsync($"/v1/events?object_id={id}&limit=100")).JsonAsync();
        Assert.Contains(events.GetProperty("data").EnumerateArray(), e => e.GetProperty("type").GetString() == EventTypes.PayoutReturned);
        Assert.Equal(HttpStatusCode.OK, (await acme.Client.DeleteAsync($"/v1/users/{user}/payout_accounts/{payout}")).StatusCode);
    }

    [Fact]
    public async Task A_cash_out_the_reserve_cannot_cover_in_that_currency_is_refused()
    {
        // Points bought with rand, cashed out in dollars: the dollar reserve holds nothing for them.
        var acme = await app.CreateIntegratorAsync();
        var (user, wallet) = await acme.HolderAsync();
        await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = await acme.DepositAccountAsync(user, wallet), amount = "5000.00" });
        var usdBefore = await HouseBalanceAsync(LedgerAccountKind.PointsReserve, "usd");
        var payout = (await (await acme.PostAsync($"/v1/users/{user}/payout_accounts", new
        {
            type = "paypal", currency = "usd", country = "ZA", rail = "paypal", account_owner_name = "Alice Moyo", paypal = new { email = "alice@example.com" }
        })).JsonAsync()).GetProperty("id").GetString();

        // Asks for more dollars than the reserve holds, from whatever other tests have left in it.
        var points = Math.Min(5000m, Math.Floor((usdBefore + 100m) / 0.055m));
        var response = await acme.CashOutAsync(wallet, payout!, points.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));

        Assert.Equal("insufficient_liquidity", await response.ErrorCodeAsync());
        Assert.Equal("5000.00", (await acme.BalanceAsync(user, wallet)).GetProperty("cashable").GetString());
    }

    [Fact]
    public async Task Only_points_are_cashed_out()
    {
        var acme = await app.CreateIntegratorAsync();
        var (user, _) = await acme.HolderAsync();
        var coin = await acme.WalletAsync(user, Accounts.Coin);
        var payout = (await (await acme.PostAsync($"/v1/users/{user}/payout_accounts", BankAccount())).JsonAsync()).GetProperty("id").GetString();

        var response = await acme.CashOutAsync(coin, payout!, "1.00");

        Assert.Equal("unsupported_route", await response.ErrorCodeAsync());
    }
}
