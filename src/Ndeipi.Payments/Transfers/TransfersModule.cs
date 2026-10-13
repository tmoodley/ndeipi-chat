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
    public static void MapTransfers(this RouteGroupBuilder v1)
    {
        v1.MapPost("/transfers", Stubs.Milestone("M3"));
        v1.MapGet("/transfers", Stubs.Milestone("M3"));
        v1.MapGet("/transfers/{transfer_id}", Stubs.Milestone("M3"));
        v1.MapPost("/transfers/{transfer_id}/cancel", Stubs.Milestone("M6"));
    }
}
