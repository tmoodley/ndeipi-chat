using System.Net;
using System.Text.Json;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// The SRS's end-to-end scenario (§9.2), all ten steps, through the public API in the sandbox,
/// with a webhook endpoint checking every signature. 1.0 ships when this passes nightly.
/// </summary>
public sealed class EndToEndTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    [Fact]
    public async Task The_three_capabilities_work_together_and_every_balance_reconciles()
    {
        var acme = await app.CreateIntegratorAsync();
        var hook = WebhookReceiver.NewUrl();
        var endpoint = await (await acme.PostAsync("/v1/webhook_endpoints", new { url = hook })).JsonAsync();
        await acme.PatchAsync($"/v1/webhook_endpoints/{endpoint.GetProperty("id").GetString()}", new { status = "enabled" });
        var publicKey = endpoint.GetProperty("public_key_pem").GetString()!;

        // 1. Create users A and B and issue onboarding links for each.
        var a = await acme.UserAsync(approved: false);
        var b = await acme.UserAsync(approved: false);
        foreach (var user in new[] { a, b })
            Assert.Equal(HttpStatusCode.Created, (await acme.PostAsync($"/v1/users/{user}/onboarding_links", null)).StatusCode);

        // 2. Money cannot move while either user is unapproved: neither can even open a wallet.
        Assert.Equal("user_not_approved", await (await acme.PostAsync($"/v1/users/{a}/wallets", new { asset = Accounts.Points })).ErrorCodeAsync());

        // 3. Approve both through the sandbox call and create a wallet for each.
        foreach (var user in new[] { a, b })
            await acme.PostAsync($"/v1/sandbox/users/{user}/kyc", new { kyc_status = "approved" });
        var walletA = await acme.WalletAsync(a);
        var walletB = await acme.WalletAsync(b);

        // ...and a transfer is refused while one of them is back under review.
        await acme.FundAsync(walletA, earned: 1m);
        await acme.PostAsync($"/v1/sandbox/users/{b}/kyc", new { kyc_status = "under_review" });
        Assert.Equal("user_not_approved", await (await acme.SendAsync(walletA, walletB, "1.00")).ErrorCodeAsync());
        await acme.PostAsync($"/v1/sandbox/users/{b}/kyc", new { kyc_status = "approved" });
        await acme.SendAsync(walletA, walletB, "1.00");
        await acme.SendAsync(walletB, walletA, "1.00");

        // 4. Create a deposit account for A and simulate a deposit of 100.00.
        var depositAccount = await acme.DepositAccountAsync(a, walletA);
        var deposit = await (await acme.PostAsync("/v1/sandbox/deposits", new { deposit_account_id = depositAccount, amount = "100.00" })).JsonAsync();

        // 5. A's wallet is credited and a deposit event arrives with a valid signature.
        Assert.Equal("101.00", (await acme.BalanceAsync(a, walletA)).GetProperty("available").GetString());
        var depositEvent = (await app.Webhooks.WaitForAsync(hook, match: r => r.Json.GetProperty("type").GetString() == EventTypes.DepositReceived)).Single();
        Assert.True(WebhookSigner.Verify(depositEvent.Body, depositEvent.Signature, publicKey, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10)));
        Assert.Equal(deposit.GetProperty("id").GetString(), depositEvent.Json.GetProperty("data").GetProperty("id").GetString());

        // 6. Send 25.00 from A to B with an idempotency key, then repeat the call with the same key.
        var key = Guid.NewGuid().ToString();
        var sent = await acme.SendAsync(walletA, walletB, "25.00", key);
        var repeated = await acme.SendAsync(walletA, walletB, "25.00", key);

        // 7. Exactly one transfer exists and balances moved once.
        Assert.Equal(await sent.Content.ReadAsStringAsync(), await repeated.Content.ReadAsStringAsync());
        var sends = await (await acme.GetAsync($"/v1/transfers?wallet_id={walletB}&kind=user_to_user&limit=100")).JsonAsync();
        Assert.Single(sends.GetProperty("data").EnumerateArray(), t => t.GetProperty("amount").GetString() == "25.00");
        Assert.Equal("76.00", (await acme.BalanceAsync(a, walletA)).GetProperty("available").GetString());
        // A's one earned point went first and stays earned, so 24 of B's 25 are cashable.
        var afterSend = await acme.BalanceAsync(b, walletB);
        Assert.Equal("25.00", afterSend.GetProperty("available").GetString());
        Assert.Equal("24.00", afterSend.GetProperty("cashable").GetString());

        // 8. Register a payout account for B and off-ramp 20.00.
        var payout = (await (await acme.PostAsync($"/v1/users/{b}/payout_accounts", new
        {
            type = "bank", currency = "zar", country = "ZA", rail = "absa_eft", account_owner_name = "Bea Ncube",
            bank = new { bank_name = "Absa", account_number = "4071234567" }
        })).JsonAsync()).GetProperty("id").GetString()!;
        var offramp = (await (await acme.CashOutAsync(walletB, payout, "20.00")).JsonAsync()).GetProperty("id").GetString();
        Assert.Equal("20.00", (await acme.BalanceAsync(b, walletB)).GetProperty("pending").GetString());

        // 9. Set the payout outcome to returned; B's wallet is refunded.
        var refunded = await (await acme.PostAsync($"/v1/sandbox/transfers/{offramp}/payout_outcome", new { outcome = "returned" })).JsonAsync();
        Assert.Equal("refunded", refunded.GetProperty("state").GetString());
        var bBalance = await acme.BalanceAsync(b, walletB);
        Assert.Equal("25.00", bBalance.GetProperty("available").GetString());
        Assert.Equal("0.00", bBalance.GetProperty("pending").GetString());

        // 10. Every wallet's history sums to its balance, bucket by bucket.
        foreach (var (user, wallet) in new[] { (a, walletA), (b, walletB) })
        {
            var history = (await (await acme.GetAsync($"/v1/users/{user}/wallets/{wallet}/history?limit=100")).JsonAsync())
                .GetProperty("data").EnumerateArray().ToList();
            var balance = await acme.BalanceAsync(user, wallet);
            decimal Sum(params string[] buckets) => history.Where(e => buckets.Contains(e.GetProperty("bucket").GetString()))
                .Sum(e => decimal.Parse(e.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            Assert.Equal(decimal.Parse(balance.GetProperty("available").GetString()!, System.Globalization.CultureInfo.InvariantCulture), Sum("available", "earned"));
            Assert.Equal(decimal.Parse(balance.GetProperty("pending").GetString()!, System.Globalization.CultureInfo.InvariantCulture), Sum("pending"));
            Assert.Equal(decimal.Parse(balance.GetProperty("cashable").GetString()!, System.Globalization.CultureInfo.InvariantCulture), Sum("available"));
        }
    }
}
