using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Transfers;

/// <summary>
/// One transfer resource for every money movement (DC-04): wallet to another user's wallet is
/// user-to-user (M3, posted on the ledger without a provider, SRV-LED-04); wallet to the same user's
/// wallet in another asset is a NdeipiCoin conversion at a locked quote (M4, from Ndeipi's stock);
/// fiat to wallet is on-ramp and wallet to payout account is off-ramp (M4, on a fiat rail).
/// </summary>
public static class TransfersModule
{
    public static IServiceCollection AddTransfers(this IServiceCollection services) =>
        services.AddScoped<TransferBook>().AddScoped<TransactionMonitor>().AddScoped<TransferService>();

    public static void MapTransfers(this RouteGroupBuilder v1)
    {
        v1.MapPost("/transfers", async (HttpContext http, TransferCreateRequest request, TransferService transfers, CancellationToken ct) =>
            await transfers.CreateAsync(request, IdempotencyMiddleware.KeyFor(http), ct) switch
            {
                TransferPreviewDto preview => Results.Json(preview, PaymentsJson.Options),
                var created => Results.Json(created, PaymentsJson.Options, statusCode: 201)
            });

        v1.MapGet("/transfers", async (
            HttpRequest http, TransferService transfers, string? user_id, string? wallet_id, string? state, string? kind,
            string? integrator_reference, DateTimeOffset? created_after, DateTimeOffset? created_before, CancellationToken ct) =>
            Results.Json(await transfers.ListAsync(
                PageRequest.From(http), user_id, wallet_id,
                WireEnum.Parse<TransferState>(state, "state"), WireEnum.Parse<TransferKind>(kind, "kind"),
                integrator_reference, created_after, created_before, ct), PaymentsJson.Options));

        v1.MapGet("/transfers/{transfer_id}", async (string transfer_id, TransferService transfers, CancellationToken ct) =>
            Results.Json(await transfers.GetAsync(transfer_id, ct), PaymentsJson.Options));

        v1.MapPost("/transfers/{transfer_id}/cancel", Stubs.Milestone("M6"));
    }
}
