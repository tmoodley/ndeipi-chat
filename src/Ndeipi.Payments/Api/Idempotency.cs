using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Api;

/// <summary>
/// Every POST carries an <c>Idempotency-Key</c> (FR-CORE-04). The first request with a key runs and
/// its response is stored; a retry with the same key and request gets that response back with
/// <c>Idempotent-Replayed: true</c> for 24 hours. The same key with a different request is refused
/// (FR-CORE-05), as is a retry while the first is still running.
///
/// Responses of 500 and above are not stored, so a retry after a server failure runs again. That is
/// safe because postings are unique per idempotency key (NFR-REL-01): the retry cannot move funds
/// a second time even if the first attempt committed before failing.
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next)
{
    public const string Header = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    const string KeyItem = "payments.idempotency_key";

    public static string? KeyFor(HttpContext http) => http.Items[KeyItem] as string;

    public async Task InvokeAsync(HttpContext http, PaymentsDbContext db, IntegratorScope scope, IOptions<PaymentsOptions> options, TimeProvider clock)
    {
        if (!HttpMethods.IsPost(http.Request.Method) || scope.IntegratorId is null)
        {
            await next(http);
            return;
        }

        var key = http.Request.Headers[Header].ToString();
        if (key.Length == 0)
        {
            await ApiErrors.WriteAsync(http, 400, new("idempotency_key_missing", "Every POST needs an Idempotency-Key header.", Header));
            return;
        }
        if (key.Length > 255)
        {
            await ApiErrors.WriteAsync(http, 400, new("invalid_request", "Idempotency-Key is at most 255 characters.", Header));
            return;
        }
        http.Items[KeyItem] = key;

        var hash = await HashRequestAsync(http.Request);
        var now = clock.GetUtcNow();

        // An expired key is free to use again.
        await db.IdempotencyRecords.Where(r => r.Key == key && r.ExpiresAt <= now).ExecuteDeleteAsync(http.RequestAborted);

        var record = new IdempotencyRecord
        {
            IntegratorId = scope.IntegratorId.Value,
            Key = key,
            RequestHash = hash,
            CreatedAt = now,
            ExpiresAt = now + options.Value.IdempotencyWindow
        };
        db.IdempotencyRecords.Add(record);
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // Someone already holds this key: replay, or refuse.
            db.ChangeTracker.Clear();
            var existing = await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, http.RequestAborted);
            if (existing is null)
                throw;
            await ReplayOrRefuseAsync(http, existing, hash);
            return;
        }
        db.Entry(record).State = EntityState.Detached;

        var body = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            await next(http);
        }
        catch
        {
            await ReleaseAsync(db, record.Id);
            throw;
        }
        finally
        {
            http.Response.Body = body;
        }

        var status = http.Response.StatusCode;
        if (status >= 500)
            await ReleaseAsync(db, record.Id);
        else
        {
            var stored = Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            await db.IdempotencyRecords.Where(r => r.Id == record.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.ResponseStatus, status)
                .SetProperty(r => r.ResponseBody, stored), CancellationToken.None);
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(body, http.RequestAborted);
    }

    static Task ReleaseAsync(PaymentsDbContext db, long id) =>
        db.IdempotencyRecords.IgnoreQueryFilters().Where(r => r.Id == id).ExecuteDeleteAsync(CancellationToken.None);

    static async Task ReplayOrRefuseAsync(HttpContext http, IdempotencyRecord existing, byte[] hash)
    {
        if (!CryptographicOperations.FixedTimeEquals(existing.RequestHash, hash))
        {
            await ApiErrors.WriteAsync(http, 409, new("idempotency_key_reused", "This Idempotency-Key was used with a different request.", Header));
            return;
        }
        if (existing.ResponseStatus is not { } status)
        {
            await ApiErrors.WriteAsync(http, 409, new("idempotency_request_in_progress", "The first request with this Idempotency-Key is still running. Retry shortly.", Header));
            return;
        }

        http.Response.StatusCode = status;
        http.Response.Headers[ReplayedHeader] = "true";
        if (!string.IsNullOrEmpty(existing.ResponseBody))
        {
            http.Response.ContentType = "application/json; charset=utf-8";
            await http.Response.WriteAsync(existing.ResponseBody, http.RequestAborted);
        }
    }

    static async Task<byte[]> HashRequestAsync(HttpRequest request)
    {
        request.EnableBuffering();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}{request.QueryString}\n"));
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, request.HttpContext.RequestAborted)) > 0)
            sha.AppendData(chunk, 0, read);
        request.Body.Position = 0;
        return sha.GetHashAndReset();
    }
}
