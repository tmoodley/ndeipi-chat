using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Api;

/// <summary>
/// Writes every state-changing call (SRV-OPS-03), and every read of identity data (SRV-KYC-05), to
/// the append-only audit log: the integrator or operator, key, request ID, route, idempotency key and
/// outcome. Bodies are never recorded (NFR-SEC-03).
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, PaymentsDbContext db, IntegratorScope scope, TimeProvider clock)
    {
        var read = HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method);
        if (read && !ReadsIdentityData(http.Request.Path))
        {
            await next(http);
            return;
        }

        var status = 500;
        try
        {
            await next(http);
            status = http.Response.StatusCode;
        }
        finally
        {
            // Whatever the endpoint left in the change tracker is done with; record only the entry.
            db.ChangeTracker.Clear();
            db.AuditLog.Add(new AuditEntry
            {
                IntegratorId = scope.IntegratorId,
                ApiKeyId = scope.ApiKeyId,
                Operator = http.User.FindFirst(Ops.OperatorAuthenticationHandler.NameClaim)?.Value,
                RequestId = RequestIds.Get(http),
                Method = http.Request.Method,
                Path = Truncate(http.Request.Path.Value ?? "", 400),
                StatusCode = status,
                IdempotencyKey = IdempotencyMiddleware.KeyFor(http),
                CreatedAt = clock.GetUtcNow()
            });
            await db.SaveChangesAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Reads that can return identity data: users, their payout accounts and everything under them,
    /// and the operator API. Every one is logged (SRV-KYC-05), so who looked at whose details is known.
    /// </summary>
    static bool ReadsIdentityData(PathString path) =>
        path.StartsWithSegments("/v1/users", StringComparison.Ordinal) || path.StartsWithSegments("/ops", StringComparison.Ordinal);

    static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
