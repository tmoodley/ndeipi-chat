using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Providers;

namespace Ndeipi.Payments.Ramps;

/// <summary>
/// On-ramp and off-ramp (SRS §4.4 to §4.6): standing deposit accounts, payout accounts, routes and
/// rates. Built in M4. Fiat moves on a rail from <see cref="ProviderRegistry"/> (Absa, PayPal) and
/// becomes Ndeipi Points, or back, at the fixed price in <see cref="PointsPricing"/>.
/// </summary>
public static class RampsModule
{
    public static IServiceCollection AddRamps(this IServiceCollection services)
    {
        services.AddSingleton<PointsPricing>();
        services.AddScoped<Treasury.OtcDesk>();
        services.AddScoped<Treasury.CoinPricing>();
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
        return services;
    }

    public static void MapRamps(this RouteGroupBuilder v1)
    {
        v1.MapPost("/users/{user_id}/deposit_accounts", Stubs.Milestone("M4"));
        v1.MapGet("/users/{user_id}/deposit_accounts", Stubs.Milestone("M4"));
        v1.MapGet("/users/{user_id}/deposit_accounts/{deposit_account_id}", Stubs.Milestone("M4"));
        v1.MapPost("/users/{user_id}/deposit_accounts/{deposit_account_id}/deactivate", Stubs.Milestone("M4"));
        v1.MapPost("/users/{user_id}/deposit_accounts/{deposit_account_id}/reactivate", Stubs.Milestone("M6"));
        v1.MapGet("/users/{user_id}/deposit_accounts/{deposit_account_id}/activity", Stubs.Milestone("M4"));

        v1.MapPost("/users/{user_id}/payout_accounts", Stubs.Milestone("M4"));
        v1.MapGet("/users/{user_id}/payout_accounts", Stubs.Milestone("M4"));
        v1.MapGet("/users/{user_id}/payout_accounts/{payout_account_id}", Stubs.Milestone("M4"));
        v1.MapDelete("/users/{user_id}/payout_accounts/{payout_account_id}", Stubs.Milestone("M4"));

        v1.MapGet("/routes", Stubs.Milestone("M4"));
        v1.MapGet("/rates", Stubs.Milestone("M4"));

        // NdeipiCoin conversions: priced by Treasury.CoinPricing, settled from Ndeipi's stock.
        v1.MapPost("/quotes", Stubs.Milestone("M4"));
        v1.MapGet("/quotes/{quote_id}", Stubs.Milestone("M4"));
    }
}
