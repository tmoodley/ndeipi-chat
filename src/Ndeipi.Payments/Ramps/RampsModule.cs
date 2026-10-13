using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Providers;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Treasury;

namespace Ndeipi.Payments.Ramps;

/// <summary>
/// On-ramp and off-ramp (SRS §4.4 to §4.6, built in M4): deposit accounts, payout accounts, routes,
/// rates, and NdeipiCoin quotes. Fiat moves on a rail from <see cref="ProviderRegistry"/> (Absa,
/// PayPal) and becomes Ndeipi Points, or back, at the fixed price in <see cref="PointsPricing"/>.
/// </summary>
public static class RampsModule
{
    public static IServiceCollection AddRamps(this IServiceCollection services)
    {
        services.AddSingleton<PointsPricing>();
        services.AddSingleton<SimulatedFiatRail>();
        services.AddSingleton<SimulatedExchange>();
        services.AddSingleton<PayPalRail>();
        services.AddSingleton<AbsaRail>();
        services.AddSingleton<BlockfinexExchange>();

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<PaymentsOptions>>().Value;
            if (options.FiatRails.Length == 0)
                throw new InvalidOperationException("Set Payments:FiatRails: \"simulated\", or \"paypal\" and \"absa\".");
            var rails = options.FiatRails.Select<string, IFiatRail>(name => name switch
            {
                "simulated" => sp.GetRequiredService<SimulatedFiatRail>(),
                "paypal" => sp.GetRequiredService<PayPalRail>(),
                "absa" => sp.GetRequiredService<AbsaRail>(),
                var other => throw new InvalidOperationException($"Unknown fiat rail provider '{other}' in Payments:FiatRails.")
            });
            IExchangeProvider? exchange = options.Exchange switch
            {
                "none" => null,
                "simulated" => sp.GetRequiredService<SimulatedExchange>(),
                "blockfinex" => sp.GetRequiredService<BlockfinexExchange>(),
                var other => throw new InvalidOperationException($"Unknown exchange '{other}' in Payments:Exchange.")
            };
            return new ProviderRegistry(rails, exchange);
        });

        services.AddScoped<RampCatalog>();
        services.AddScoped<RateService>();
        services.AddScoped<HouseAccounts>();
        services.AddSingleton<ISanctionsScreening, SimulatedSanctionsScreening>();
        services.AddScoped<PayoutAccountService>();
        services.AddScoped<OnRampService>();
        services.AddScoped<OffRampService>();
        services.AddScoped<OtcDesk>();
        services.AddScoped<CoinPricing>();
        services.AddScoped<QuoteService>();
        services.AddScoped<ConversionService>();
        services.AddScoped<TreasuryService>();
        services.AddSingleton<RampWorker>();
        services.AddHostedService(sp => sp.GetRequiredService<RampWorker>());
        return services;
    }

    public static void MapRamps(this RouteGroupBuilder v1)
    {
        v1.MapPost("/users/{user_id}/deposit_accounts", async (string user_id, DepositAccountCreateRequest request, OnRampService ramps, CancellationToken ct) =>
            Results.Json(await ramps.CreateAccountAsync(user_id, request, ct), PaymentsJson.Options, statusCode: 201));
        v1.MapGet("/users/{user_id}/deposit_accounts", async (string user_id, HttpRequest http, OnRampService ramps, CancellationToken ct) =>
            Results.Json(await ramps.ListAccountsAsync(user_id, PageRequest.From(http), ct), PaymentsJson.Options));
        v1.MapGet("/users/{user_id}/deposit_accounts/{deposit_account_id}", async (string user_id, string deposit_account_id, OnRampService ramps, CancellationToken ct) =>
            Results.Json(await ramps.GetAccountAsync(user_id, deposit_account_id, ct), PaymentsJson.Options));
        v1.MapPost("/users/{user_id}/deposit_accounts/{deposit_account_id}/deactivate", async (string user_id, string deposit_account_id, OnRampService ramps, CancellationToken ct) =>
            Results.Json(await ramps.SetActiveAsync(user_id, deposit_account_id, active: false, ct), PaymentsJson.Options));
        v1.MapPost("/users/{user_id}/deposit_accounts/{deposit_account_id}/reactivate", async (string user_id, string deposit_account_id, OnRampService ramps, CancellationToken ct) =>
            Results.Json(await ramps.SetActiveAsync(user_id, deposit_account_id, active: true, ct), PaymentsJson.Options));
        v1.MapGet("/users/{user_id}/deposit_accounts/{deposit_account_id}/activity", async (
            string user_id, string deposit_account_id, DateTimeOffset? created_after, DateTimeOffset? created_before, HttpRequest http, OnRampService ramps, CancellationToken ct) =>
            Results.Json(await ramps.ActivityAsync(user_id, deposit_account_id, PageRequest.From(http), created_after, created_before, ct), PaymentsJson.Options));

        v1.MapPost("/users/{user_id}/payout_accounts", async (string user_id, PayoutAccountCreateRequest request, PayoutAccountService accounts, CancellationToken ct) =>
            Results.Json(await accounts.CreateAsync(user_id, request, ct), PaymentsJson.Options, statusCode: 201));
        v1.MapGet("/users/{user_id}/payout_accounts", async (string user_id, HttpRequest http, PayoutAccountService accounts, CancellationToken ct) =>
            Results.Json(await accounts.ListAsync(user_id, PageRequest.From(http), ct), PaymentsJson.Options));
        v1.MapGet("/users/{user_id}/payout_accounts/{payout_account_id}", async (string user_id, string payout_account_id, PayoutAccountService accounts, CancellationToken ct) =>
            Results.Json(await accounts.GetAsync(user_id, payout_account_id, ct), PaymentsJson.Options));
        v1.MapDelete("/users/{user_id}/payout_accounts/{payout_account_id}", async (string user_id, string payout_account_id, PayoutAccountService accounts, CancellationToken ct) =>
            Results.Json(await accounts.DeleteAsync(user_id, payout_account_id, ct), PaymentsJson.Options));

        v1.MapGet("/routes", (string? kind, string? country, RampCatalog catalog) =>
            Results.Json(new { data = catalog.Routes(WireEnum.Parse<TransferKind>(kind, "kind"), country) }, PaymentsJson.Options));
        v1.MapGet("/rates", async (string? from, string? to, RateService rates, CancellationToken ct) =>
        {
            if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
                throw PaymentsException.BadRequest("Send both from and to.", string.IsNullOrEmpty(from) ? "from" : "to");
            return Results.Json(await rates.GetAsync(from, to, ct), PaymentsJson.Options);
        });

        v1.MapPost("/quotes", async (QuoteCreateRequest request, QuoteService quotes, CancellationToken ct) =>
            Results.Json(await quotes.CreateAsync(request, ct), PaymentsJson.Options, statusCode: 201));
        v1.MapGet("/quotes/{quote_id}", async (string quote_id, QuoteService quotes, CancellationToken ct) =>
            Results.Json(await quotes.GetAsync(quote_id, ct), PaymentsJson.Options));
    }
}
