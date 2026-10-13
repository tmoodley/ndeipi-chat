using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Api;

/// <summary>
/// Writes every state-changing call to the append-only audit log (SRV-OPS-03): the integrator,
/// key, request ID, route, idempotency key and outcome. Bodies are never recorded (NFR-SEC-03).
/// </summary>
public sealed class AuditMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext http, PaymentsDbContext db, IntegratorScope scope, TimeProvider clock)
    {
        if (HttpMethods.IsGet(http.Request.Method) || HttpMethods.IsHead(http.Request.Method) || HttpMethods.IsOptions(http.Request.Method))
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

    static string Truncate(string value, int length) => value.Length > length ? value[..length] : value;
}
