using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
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

        sandbox.MapPost("/deposits", Stubs.Milestone("M4"));
        sandbox.MapPost("/transfers/{transfer_id}/payout_outcome", Stubs.Milestone("M4"));
    }
}
