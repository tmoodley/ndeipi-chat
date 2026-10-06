using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Ops;

public sealed record DecisionRequest(string? Reason);

/// <summary>
/// The operator API (SRV-OPS-04) at <c>/ops/v1</c>: for Ndeipi's own staff, authenticated with
/// operator keys, and not part of the integrator contract. It stands in for the operator console's
/// back end: review queue, release and reject, and four-eyes manual refunds.
/// </summary>
public static class OpsModule
{
    public static IServiceCollection AddOps(this IServiceCollection services) =>
        services.AddScoped<OperatorKeyService>().AddScoped<ReviewService>().AddScoped<RefundService>();

    public static void MapOps(this IEndpointRouteBuilder app)
    {
        var ops = app.MapGroup("/ops/v1").RequireAuthorization(OperatorAuthenticationHandler.Policy);

        ops.MapGet("/reviews", async (ReviewService reviews, CancellationToken ct) =>
            Results.Json(new { data = await reviews.HeldAsync(ct) }, PaymentsJson.Options));
        ops.MapPost("/transfers/{transfer_id}/release", async (string transfer_id, ReviewService reviews, CancellationToken ct) =>
            Results.Json(await reviews.ReleaseAsync(transfer_id, ct), PaymentsJson.Options));
        ops.MapPost("/transfers/{transfer_id}/reject", async (string transfer_id, DecisionRequest request, ReviewService reviews, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Reason))
                throw PaymentsException.Validation([new("reason", "required", "Say why it is rejected; the integrator sees this.")]);
            return Results.Json(await reviews.RejectAsync(transfer_id, request.Reason.Trim(), ct), PaymentsJson.Options);
        });

        ops.MapPost("/refunds", async (HttpContext http, RefundCreateRequest request, RefundService refunds, CancellationToken ct) =>
            Results.Json(await refunds.RequestAsync(request, OperatorAuthenticationHandler.Name(http), ct), PaymentsJson.Options, statusCode: 201));
        ops.MapGet("/refunds", async (string? status, RefundService refunds, CancellationToken ct) =>
            Results.Json(new { data = await refunds.ListAsync(WireEnum.Parse<RefundStatus>(status, "status"), ct) }, PaymentsJson.Options));
        ops.MapPost("/refunds/{refund_id}/approve", async (HttpContext http, string refund_id, RefundService refunds, CancellationToken ct) =>
            Results.Json(await refunds.DecideAsync(refund_id, approve: true, OperatorAuthenticationHandler.Name(http), ct), PaymentsJson.Options));
        ops.MapPost("/refunds/{refund_id}/decline", async (HttpContext http, string refund_id, RefundService refunds, CancellationToken ct) =>
            Results.Json(await refunds.DecideAsync(refund_id, approve: false, OperatorAuthenticationHandler.Name(http), ct), PaymentsJson.Options));
    }
}
