using System.Net;
using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// User-to-user transfers (SRS §4.3): settled on the ledger in one transaction (FR-XFER-02,
/// SRV-LED-04), refused with a distinct error per cause (FR-XFER-03), retried safely (NFR-REL-01),
/// and points keeping their kind so rewards never become cashable by changing hands.
/// </summary>
public sealed class TransfersTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    [Fact]
    public async Task Sending_points_completes_at_once_with_a_receipt_and_an_event()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 100m);

        var response = await acme.PostAsync("/v1/transfers", new
        {
            source = new { type = "wallet", wallet_id = aliceWallet },
            destination = new { type = "wallet", wallet_id = bobWallet },
            amount = "25.00",
            integrator_reference = "invoice-7781",
            metadata = new Dictionary<string, string> { ["note"] = "lunch" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var transfer = await response.JsonAsync();
        Assert.StartsWith("trf_", transfer.GetProperty("id").GetString());
        Assert.Equal("user_to_user", transfer.GetProperty("kind").GetString());
        Assert.Equal("completed", transfer.GetProperty("state").GetString());
        Assert.Equal("25.00", transfer.GetProperty("amount").GetString());
        Assert.Equal(alice, transfer.GetProperty("source").GetProperty("user_id").GetString());
        Assert.Equal(bob, transfer.GetProperty("destination").GetProperty("user_id").GetString());
        Assert.Equal("25.00", transfer.GetProperty("receipt").GetProperty("amount_debited").GetString());
        Assert.Equal("25.00", transfer.GetProperty("receipt").GetProperty("amount_credited").GetString());
        Assert.Equal("0.00", transfer.GetProperty("receipt").GetProperty("earned_amount").GetString());
        Assert.Equal("invoice-7781", transfer.GetProperty("integrator_reference").GetString());
        Assert.Equal("lunch", transfer.GetProperty("metadata").GetProperty("note").GetString());

        Assert.Equal("75.00", (await acme.BalanceAsync(alice, aliceWallet)).GetProperty("available").GetString());
        Assert.Equal("25.00", (await acme.BalanceAsync(bob, bobWallet)).GetProperty("cashable").GetString());
        var id = transfer.GetProperty("id").GetString();
        var events = await (await acme.GetAsync($"/v1/events?object_id={id}")).JsonAsync();
        Assert.Equal(EventTypes.TransferCreated, events.GetProperty("data")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Earned_points_go_first_and_arrive_as_earned_points()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 50m, earned: 10m);

        var transfer = await (await acme.SendAsync(aliceWallet, bobWallet, "25.00")).JsonAsync();

        Assert.Equal("10.00", transfer.GetProperty("receipt").GetProperty("earned_amount").GetString());
        var alices = await acme.BalanceAsync(alice, aliceWallet);
        var bobs = await acme.BalanceAsync(bob, bobWallet);
        Assert.Equal("35.00", alices.GetProperty("available").GetString());
        Assert.Equal("35.00", alices.GetProperty("cashable").GetString());
        Assert.Equal("25.00", bobs.GetProperty("available").GetString());
        Assert.Equal("15.00", bobs.GetProperty("cashable").GetString());
    }

    [Fact]
    public async Task Too_little_balance_is_refused_and_moves_nothing()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (_, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 10m, earned: 5m);

        var response = await acme.SendAsync(aliceWallet, bobWallet, "15.01");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("insufficient_funds", await response.ErrorCodeAsync());
        Assert.Equal("15.00", (await acme.BalanceAsync(alice, aliceWallet)).GetProperty("available").GetString());
        Assert.Equal(0, await app.DbAsync(db => db.Transfers.IgnoreQueryFilters().CountAsync(t => t.SourceWalletId == aliceWallet)));
    }

    [Fact]
    public async Task Each_invalid_pairing_has_its_own_error()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var aliceCoin = await acme.WalletAsync(alice, Accounts.Coin);
        var bob = await acme.UserAsync();
        var bobCoin = await acme.WalletAsync(bob, Accounts.Coin);
        await acme.FundAsync(aliceWallet, purchased: 10m);

        var same = await acme.SendAsync(aliceWallet, aliceWallet, "1.00");
        var mismatch = await acme.SendAsync(aliceWallet, bobCoin, "1.00");
        var conversion = await acme.SendAsync(aliceWallet, aliceCoin, "1.00");
        var precision = await acme.SendAsync(aliceWallet, (await acme.HolderAsync()).Wallet, "1.001");

        Assert.Equal("same_source_and_destination", await same.ErrorCodeAsync());
        Assert.Equal("asset_mismatch", await mismatch.ErrorCodeAsync());
        Assert.Equal((HttpStatusCode)501, conversion.StatusCode); // conversions arrive in M4
        Assert.Equal("invalid_amount_precision", await precision.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_recipient_who_has_not_finished_verification_cannot_receive()
    {
        var acme = await app.CreateIntegratorAsync();
        var (_, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 10m);
        await acme.PostAsync($"/v1/sandbox/users/{bob}/kyc", new { kyc_status = "under_review" });

        var response = await acme.SendAsync(aliceWallet, bobWallet, "1.00");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var error = await response.JsonAsync();
        Assert.Equal("user_not_approved", error.GetProperty("code").GetString());
        Assert.Contains("recipient", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_retried_send_moves_the_funds_once()
    {
        // SRS §9.2 steps 6 and 7.
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 100m);
        var key = Guid.NewGuid().ToString();

        var first = await acme.SendAsync(aliceWallet, bobWallet, "25.00", key);
        var retry = await acme.SendAsync(aliceWallet, bobWallet, "25.00", key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues("Idempotent-Replayed").Single());
        Assert.Equal(1, await app.DbAsync(db => db.Transfers.IgnoreQueryFilters().CountAsync(t => t.SourceWalletId == aliceWallet)));
        Assert.Equal("75.00", (await acme.BalanceAsync(alice, aliceWallet)).GetProperty("available").GetString());
        Assert.Equal("25.00", (await acme.BalanceAsync(bob, bobWallet)).GetProperty("available").GetString());
    }

    [Fact]
    public async Task A_dry_run_checks_everything_and_moves_nothing()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (_, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 20m, earned: 5m);

        var preview = await acme.SendAsync(aliceWallet, bobWallet, "10.00", dryRun: true);
        var tooMuch = await acme.SendAsync(aliceWallet, bobWallet, "30.00", dryRun: true);

        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = await preview.JsonAsync();
        Assert.Equal("transfer_preview", body.GetProperty("object").GetString());
        Assert.Equal("5.00", body.GetProperty("estimated").GetProperty("earned_amount").GetString());
        Assert.Equal("insufficient_funds", await tooMuch.ErrorCodeAsync());
        Assert.Equal("25.00", (await acme.BalanceAsync(alice, aliceWallet)).GetProperty("available").GetString());
    }

    [Fact]
    public async Task Transfers_can_be_fetched_and_filtered_by_user_wallet_and_state()
    {
        var acme = await app.CreateIntegratorAsync();
        var zed = await app.CreateIntegratorAsync("Zed");
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (_, bobWallet) = await acme.HolderAsync();
        var (_, carolWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 100m);
        var toBob = (await (await acme.SendAsync(aliceWallet, bobWallet, "1.00")).JsonAsync()).GetProperty("id").GetString();
        var toCarol = (await (await acme.SendAsync(aliceWallet, carolWallet, "2.00")).JsonAsync()).GetProperty("id").GetString();

        static async Task<List<string>> IdsAsync(HttpResponseMessage r) =>
            [.. (await r.JsonAsync()).GetProperty("data").EnumerateArray().Select(t => t.GetProperty("id").GetString()!)];

        Assert.Equal(new[] { toCarol, toBob }, await IdsAsync(await acme.GetAsync($"/v1/transfers?user_id={alice}")));
        Assert.Equal(new[] { toBob }, await IdsAsync(await acme.GetAsync($"/v1/transfers?wallet_id={bobWallet}")));
        Assert.Equal(2, (await IdsAsync(await acme.GetAsync($"/v1/transfers?user_id={alice}&state=completed&kind=user_to_user"))).Count);
        Assert.Empty(await IdsAsync(await acme.GetAsync($"/v1/transfers?user_id={alice}&state=in_review")));
        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync($"/v1/transfers/{toBob}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await zed.GetAsync($"/v1/transfers/{toBob}")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_sends_never_overspend_and_history_still_sums_to_the_balance()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 30m, earned: 20m);

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => acme.SendAsync(aliceWallet, bobWallet, "5.00")));

        Assert.Equal(10, results.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.All(results.Where(r => r.StatusCode != HttpStatusCode.Created), r => Assert.Equal(HttpStatusCode.UnprocessableEntity, r.StatusCode));
        Assert.Equal("0.00", (await acme.BalanceAsync(alice, aliceWallet)).GetProperty("available").GetString());
        var bobs = await acme.BalanceAsync(bob, bobWallet);
        Assert.Equal("50.00", bobs.GetProperty("available").GetString());
        Assert.Equal("30.00", bobs.GetProperty("cashable").GetString());

        var history = await (await acme.GetAsync($"/v1/users/{bob}/wallets/{bobWallet}/history?limit=100")).JsonAsync();
        Assert.Equal(50m, history.GetProperty("data").EnumerateArray().Sum(e => decimal.Parse(e.GetProperty("amount").GetString()!)));
    }

    [Fact]
    public async Task A_user_with_funds_cannot_be_deactivated_and_a_deactivated_user_cannot_receive()
    {
        var acme = await app.CreateIntegratorAsync();
        var (alice, aliceWallet) = await acme.HolderAsync();
        var (bob, bobWallet) = await acme.HolderAsync();
        await acme.FundAsync(aliceWallet, purchased: 10m);

        var refused = await acme.PostAsync($"/v1/users/{alice}/deactivate", null);
        await acme.SendAsync(aliceWallet, bobWallet, "10.00");
        var deactivated = await acme.PostAsync($"/v1/users/{alice}/deactivate", null);
        var again = await acme.PostAsync($"/v1/users/{alice}/deactivate", null);
        var toAlice = await acme.SendAsync(bobWallet, aliceWallet, "1.00");

        Assert.Equal("user_has_balance", await refused.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.Equal("deactivated", (await deactivated.JsonAsync()).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("frozen", (await (await acme.GetAsync($"/v1/users/{alice}/wallets/{aliceWallet}")).JsonAsync()).GetProperty("status").GetString());
        Assert.Equal("user_deactivated", await toAlice.ErrorCodeAsync());
        Assert.Equal(1, await app.DbAsync(db => db.Events.IgnoreQueryFilters().CountAsync(e => e.ObjectId == alice && e.Type == EventTypes.UserDeactivated)));
        Assert.Equal("10.00", (await acme.BalanceAsync(bob, bobWallet)).GetProperty("available").GetString());
    }
}
