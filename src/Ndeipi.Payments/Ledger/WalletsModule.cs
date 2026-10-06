using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Ledger;

/// <summary>
/// Wallets (SRS §4.2, built in M3): one per user per asset, each a set of ledger accounts
/// (<see cref="WalletService"/>), on the double-entry <see cref="LedgerService"/>.
/// </summary>
public static class WalletsModule
{
    public static IServiceCollection AddLedger(this IServiceCollection services)
    {
        services.AddScoped<LedgerService>();
        services.AddScoped<WalletService>();
        services.AddScoped<PointsIssuance>();
        return services;
    }

    public static void MapWallets(this RouteGroupBuilder v1)
    {
        v1.MapPost("/users/{user_id}/wallets", async (string user_id, WalletCreateRequest request, WalletService wallets, CancellationToken ct) =>
            Results.Json(await wallets.CreateAsync(user_id, request, ct), PaymentsJson.Options, statusCode: 201));
        v1.MapGet("/users/{user_id}/wallets", async (string user_id, string? asset, HttpRequest http, WalletService wallets, CancellationToken ct) =>
            Results.Json(await wallets.ListAsync(user_id, asset, PageRequest.From(http), ct), PaymentsJson.Options));
        v1.MapGet("/users/{user_id}/wallets/{wallet_id}", async (string user_id, string wallet_id, WalletService wallets, CancellationToken ct) =>
            Results.Json(await wallets.GetAsync(user_id, wallet_id, ct), PaymentsJson.Options));
        v1.MapGet("/users/{user_id}/wallets/{wallet_id}/balance", async (string user_id, string wallet_id, WalletService wallets, CancellationToken ct) =>
            Results.Json(await wallets.BalanceAsync(user_id, wallet_id, ct), PaymentsJson.Options));
        v1.MapGet("/users/{user_id}/wallets/{wallet_id}/history", async (
            string user_id, string wallet_id, DateTimeOffset? created_after, DateTimeOffset? created_before, HttpRequest http, WalletService wallets, CancellationToken ct) =>
            Results.Json(await wallets.HistoryAsync(user_id, wallet_id, PageRequest.From(http), created_after, created_before, ct), PaymentsJson.Options));
    }
}
