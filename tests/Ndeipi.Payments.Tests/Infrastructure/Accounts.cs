using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Ledger;

namespace Ndeipi.Payments.Tests.Infrastructure;

/// <summary>Users and wallets set up the way an integrator would, plus funding that M4's ramps will replace.</summary>
public static class Accounts
{
    public const string Points = "ndeipi-points";
    public const string Coin = "ndeipi-coin";

    public static async Task<string> UserAsync(this TestIntegrator integrator, bool approved = true)
    {
        var user = (await (await integrator.PostAsync("/v1/users", new { type = "individual", external_reference = $"crm-{Guid.NewGuid():N}" })).JsonAsync())
            .GetProperty("id").GetString()!;
        if (approved)
            await integrator.PostAsync($"/v1/sandbox/users/{user}/kyc", new { kyc_status = "approved" });
        return user;
    }

    public static async Task<string> WalletAsync(this TestIntegrator integrator, string user, string asset = Points)
    {
        var response = await integrator.PostAsync($"/v1/users/{user}/wallets", new { asset, acknowledge_price_risk = asset == Coin ? true : (bool?)null });
        return (await response.JsonAsync()).GetProperty("id").GetString()!;
    }

    /// <summary>An approved user with a points wallet: (user, wallet).</summary>
    public static async Task<(string User, string Wallet)> HolderAsync(this TestIntegrator integrator)
    {
        var user = await integrator.UserAsync();
        return (user, await integrator.WalletAsync(user));
    }

    /// <summary>Credits points the way an on-ramp (purchased) or a reward (earned) will.</summary>
    public static Task FundAsync(this TestIntegrator integrator, string wallet, decimal purchased = 0, decimal earned = 0) =>
        integrator.App.AsIntegratorAsync(integrator.Id, async sp =>
        {
            var issuance = sp.GetRequiredService<PointsIssuance>();
            if (purchased > 0)
                await issuance.CreditPurchasedAsync(wallet, purchased, "Test top-up", null, default);
            if (earned > 0)
                await issuance.CreditEarnedAsync(wallet, earned, "Test reward", default);
            return 0;
        });

    public static async Task<JsonElement> BalanceAsync(this TestIntegrator integrator, string user, string wallet) =>
        await (await integrator.GetAsync($"/v1/users/{user}/wallets/{wallet}/balance")).JsonAsync();

    public static async Task<string> DepositAccountAsync(this TestIntegrator integrator, string user, string wallet) =>
        (await (await integrator.PostAsync($"/v1/users/{user}/deposit_accounts",
            new { source = new { currency = "zar", rail = "absa_eft" }, destination = new { wallet_id = wallet } })).JsonAsync()).GetProperty("id").GetString()!;

    public static Task<HttpResponseMessage> CashOutAsync(this TestIntegrator integrator, string wallet, string payoutAccount, string amount, string? key = null) =>
        integrator.PostAsync("/v1/transfers", new
        {
            source = new { type = "wallet", wallet_id = wallet },
            destination = new { type = "payout_account", payout_account_id = payoutAccount },
            amount
        }, key);

    public static Task<HttpResponseMessage> SendAsync(this TestIntegrator integrator, string fromWallet, string toWallet, string amount, string? key = null, bool dryRun = false) =>
        integrator.PostAsync("/v1/transfers", new
        {
            source = new { type = "wallet", wallet_id = fromWallet },
            destination = new { type = "wallet", wallet_id = toWallet },
            amount,
            dry_run = dryRun ? true : (bool?)null
        }, key);
}
