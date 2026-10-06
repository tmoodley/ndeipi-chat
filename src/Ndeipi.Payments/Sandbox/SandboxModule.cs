using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Providers;
using Ndeipi.Payments.Ramps;
using Ndeipi.Payments.Users;

namespace Ndeipi.Payments.Sandbox;

public sealed record SandboxKycRequest(KycStatus? KycStatus, TermsStatus? TermsStatus, List<ReasonDto>? RejectionReasons);

/// <summary>
/// Sandbox-only calls (SRS §4.10) that stand in for the outside world: a KYC outcome, an incoming
/// deposit, a payout result. They fire the same events production would (FR-SBX-03). A production
/// deployment answers every one with <c>403 sandbox_only</c>.
/// </summary>
public static class SandboxModule
{
    public static void MapSandbox(this RouteGroupBuilder v1)
    {
        var sandbox = v1.MapGroup("/sandbox").AddEndpointFilter(async (context, next) =>
        {
            var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<PaymentsOptions>>().Value;
            return options.Environment == PaymentsEnvironment.Production
                ? ApiErrors.Result(context.HttpContext, 403, new("sandbox_only", "Sandbox calls are not available in production."))
                : await next(context);
        });

        // As if the hosted KYC flow had finished (FR-SBX-02). Approving KYC approves terms too unless told otherwise.
        sandbox.MapPost("/users/{user_id}/kyc", async (string user_id, SandboxKycRequest request, UserStatusService statuses, CancellationToken ct) =>
        {
            if (request.KycStatus is not { } kyc)
                throw PaymentsException.Validation([new("kyc_status", "required", "kyc_status is required.")]);
            var terms = request.TermsStatus ?? (kyc == KycStatus.Approved ? TermsStatus.Approved : null);
            return Results.Json(await statuses.SetAsync(user_id, kyc, terms, request.RejectionReasons, ct), PaymentsJson.Options);
        });

        // Money arriving at a standing deposit account or for a one-off on-ramp, as the rail would report it.
        sandbox.MapPost("/deposits", async (SandboxDepositRequest request, OnRampService ramps, CancellationToken ct) =>
        {
            if ((request.DepositAccountId is null) == (request.TransferId is null))
                throw PaymentsException.Validation([new("deposit_account_id", "required", "Send exactly one of deposit_account_id or transfer_id.")]);
            if (request.Amount is not { } amount)
                throw PaymentsException.Validation([new("amount", "required", "amount is required.")]);
            var outcome = request.Outcome ?? DepositOutcome.Credited;
            var reference = "SIM-" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
            var transfer = request.DepositAccountId is { } account
                ? await ramps.ReceiveForAccountAsync(account, amount, outcome, reference, ct)
                : await ramps.ReceiveForTransferAsync(request.TransferId!, amount, outcome, reference, ct);
            return Results.Json(transfer, PaymentsJson.Options);
        });

        // A submitted payout's result, as the rail would report it. A payout the ramp worker has not
        // submitted yet is submitted first.
        sandbox.MapPost("/transfers/{transfer_id}/payout_outcome", async (
            string transfer_id, SandboxPayoutOutcomeRequest request, OffRampService offRamps, Transfers.TransferService transfers, SimulatedFiatRail rail, CancellationToken ct) =>
        {
            var status = request.Outcome switch
            {
                "completed" => ProviderOperationStatus.Completed,
                "returned" => ProviderOperationStatus.Returned,
                "undeliverable" => ProviderOperationStatus.Undeliverable,
                _ => throw PaymentsException.Validation([new("outcome", "invalid_value", "outcome is completed, returned or undeliverable.")])
            };
            await offRamps.SubmitAsync(transfer_id, ct);
            var reference = "SIM-" + transfer_id[^8..];
            try
            {
                rail.SetOutcome(transfer_id, status, request.Reason?.Message);
            }
            catch (KeyNotFoundException)
            {
                throw PaymentsException.Conflict("invalid_state", "That transfer is not an off-ramp waiting on its payout.");
            }
            try
            {
                return Results.Json(await offRamps.ApplyOutcomeAsync(transfer_id, status, reference, request.Reason?.Message, ct), PaymentsJson.Options);
            }
            catch (PaymentsException e) when (e.Error.Code == "invalid_state")
            {
                // The ramp worker polled the simulated rail and applied the outcome first.
                return Results.Json(await transfers.GetAsync(transfer_id, ct), PaymentsJson.Options);
            }
        });
    }
}

public sealed record SandboxDepositRequest(string? DepositAccountId, string? TransferId, decimal? Amount, DepositOutcome? Outcome);

public sealed record SandboxPayoutOutcomeRequest(string? Outcome, ReasonDto? Reason);
