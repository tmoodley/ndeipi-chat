using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Ledger;

/// <summary>
/// Wallets (SRS §4.2): one per user per asset, each a pair of ledger accounts, available and
/// pending (SRV-LED-05). Built in M3 on <see cref="LedgerService"/>.
/// </summary>
public static class WalletsModule
{
    public static IServiceCollection AddLedger(this IServiceCollection services) => services.AddScoped<LedgerService>();

    public static void MapWallets(this RouteGroupBuilder v1)
    {
        v1.MapPost("/users/{user_id}/wallets", Stubs.Milestone("M3"));
        v1.MapGet("/users/{user_id}/wallets", Stubs.Milestone("M3"));
        v1.MapGet("/users/{user_id}/wallets/{wallet_id}", Stubs.Milestone("M3"));
        v1.MapGet("/users/{user_id}/wallets/{wallet_id}/balance", Stubs.Milestone("M3"));
        v1.MapGet("/users/{user_id}/wallets/{wallet_id}/history", Stubs.Milestone("M3"));
    }
}
