using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Providers;
using Ndeipi.Payments.Reconciliation;
using Ndeipi.Payments.Tests.Infrastructure;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// Fiat on PayPal and Absa, conversion on Blockfinex: routing by rail code (SRV-PROV-09) and
/// reconciliation per provider (SRV-PROV-06).
/// </summary>
public sealed class ProviderTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    [Fact]
    public void Production_routes_paypal_and_absa_rails_to_their_own_providers()
    {
        var registry = new ProviderRegistry([new PayPalRail(), new AbsaRail()], new BlockfinexExchange());

        Assert.Equal("paypal", registry.RailFor(RailCodes.PayPal).Name);
        Assert.Equal("absa", registry.RailFor(RailCodes.AbsaEft).Name);
        Assert.Equal("blockfinex", registry.Exchange!.Name);
        var unknown = Assert.Throws<PaymentsException>(() => registry.RailFor("ecocash"));
        Assert.Equal("unsupported_route", unknown.Error.Code);
    }

    [Fact]
    public void Two_providers_cannot_serve_the_same_rail()
    {
        var clock = TimeProvider.System;
        Assert.Throws<InvalidOperationException>(() =>
            new ProviderRegistry([new PayPalRail(), new SimulatedFiatRail(clock)], new SimulatedExchange(clock)));
    }

    [Fact]
    public async Task Unbuilt_providers_refuse_calls_as_unavailable_rather_than_failing_silently()
    {
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => new PayPalRail().GetStatusAsync("ref", default));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => new AbsaRail().GetStatusAsync("ref", default));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => new BlockfinexExchange().GetRateAsync("usd", "usd-stable", default));
    }

    [Fact]
    public async Task The_sandbox_serves_the_production_rail_codes_with_matching_instruction_types()
    {
        var rail = app.Services.GetRequiredService<ProviderRegistry>();

        var paypal = await rail.RailFor(RailCodes.PayPal).StartCollectionAsync(new("trf_01M495TESTREFERENCE00001", RailCodes.PayPal, "usd", 50m, null, null), default);
        var absa = await rail.RailFor(RailCodes.AbsaEft).StartCollectionAsync(new("da_01M495TESTREFERENCE000002", RailCodes.AbsaEft, "zar", null, null, null), default);

        Assert.Equal("paypal", paypal.Type);
        Assert.StartsWith("https://", paypal.Details["approval_url"]);
        Assert.NotNull(paypal.ExpiresAt);
        Assert.Equal("bank", absa.Type);
        Assert.Contains("account_number", absa.Details.Keys);
        Assert.Null(absa.ExpiresAt);
    }

    [Fact]
    public async Task Reconciliation_flags_a_float_the_provider_does_not_report()
    {
        var acme = await app.CreateIntegratorAsync();
        await app.AsIntegratorAsync(acme.Id, async sp =>
        {
            var ledger = sp.GetRequiredService<LedgerService>();
            var absaFloat = await ledger.OpenAccountAsync(LedgerAccountKind.ProviderClearing, LedgerBucket.Available, "zar", null, default, provider: "simulated_fiat");
            var suspense = await ledger.OpenAccountAsync(LedgerAccountKind.Suspense, LedgerBucket.Pending, "zar", null, default);
            return await ledger.PostAsync(new PostingRequest("Deposit seen at the bank", [new(absaFloat.Id, -250m), new(suspense.Id, 250m)]), default);
        });

        // The simulated rail's statement reports nothing held, so the 250.00 the ledger expects is a difference.
        var report = await app.AsIntegratorAsync(acme.Id, sp => sp.GetRequiredService<ReconciliationService>().RunAsync(DateTimeOffset.UtcNow.AddDays(-1), default));

        var difference = Assert.Single(report.Differences, d => d.Asset == "zar");
        Assert.Equal("simulated_fiat", difference.Provider);
        Assert.Equal(250m, difference.Ledger);
        Assert.Equal(0m, difference.Statement);
    }
}
