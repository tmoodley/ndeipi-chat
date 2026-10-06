using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Sandbox;

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

        sandbox.MapPost("/users/{user_id}/kyc", Stubs.Milestone("M2"));
        sandbox.MapPost("/deposits", Stubs.Milestone("M4"));
        sandbox.MapPost("/transfers/{transfer_id}/payout_outcome", Stubs.Milestone("M4"));
    }
}
