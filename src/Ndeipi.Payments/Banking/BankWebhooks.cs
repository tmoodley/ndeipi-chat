using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Banking;

/// <summary>
/// Receives Meow's webhooks at <c>POST /webhooks/meow</c> (SRS §4.2). Meow signs them the Standard
/// Webhooks way: HMAC-SHA256 over <c>{webhook-id}.{webhook-timestamp}.{body}</c> with the
/// base64-decoded <c>whsec_</c> secret, sent as space-separated <c>v1,&lt;base64&gt;</c> entries.
///
/// A bad signature or a timestamp more than five minutes off is answered 401 and nothing is kept
/// (TC-NFR-SEC-01). A good one is stored, once per <c>webhook-id</c>, and answered 200 at once;
/// <see cref="BankingWorker"/> applies it, so the bank never waits on Ndeipi's processing.
/// </summary>
public static class BankWebhooks
{
    public const string Path = "/webhooks/meow";
    static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);
    const int MaxBody = 1 << 20;

    public static void MapBankWebhooks(this IEndpointRouteBuilder app) =>
        app.MapPost(Path, ReceiveAsync).AllowAnonymous().DisableAntiforgery();

    static async Task<IResult> ReceiveAsync(HttpContext http, PaymentsDbContext db, IOptions<PaymentsOptions> options, TimeProvider clock, ILoggerFactory logs)
    {
        var log = logs.CreateLogger(typeof(BankWebhooks));
        if (!options.Value.Banking.Enabled)
            return ApiErrors.Result(http, 404, new("not_found", "No such resource."));

        if (http.Request.ContentLength > MaxBody)
            return ApiErrors.Result(http, 413, new("invalid_request", "Webhook body too large."));
        using var reader = new StreamReader(http.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(http.RequestAborted);

        var id = http.Request.Headers["webhook-id"].ToString();
        var timestamp = http.Request.Headers["webhook-timestamp"].ToString();
        var signature = http.Request.Headers["webhook-signature"].ToString();
        if (!Verify(options.Value.Banking.Meow.WebhookSecret, id, timestamp, signature, body, clock.GetUtcNow()))
        {
            log.LogWarning("Rejected a bank webhook with a bad signature or timestamp (id {Id})", id.Length > 100 ? id[..100] : id);
            return ApiErrors.Result(http, 401, new("unauthorized", "Bad webhook signature."));
        }

        string type;
        try
        {
            type = JsonNode.Parse(body)?["type"]?.GetValue<string>() ?? "unknown";
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException)
        {
            return ApiErrors.Result(http, 400, new("invalid_request", "The webhook body is not JSON."));
        }

        db.BankWebhookEvents.Add(new BankWebhookEvent
        {
            EventId = id,
            EventType = type.Length > 100 ? type[..100] : type,
            RawPayload = body,
            Signature = signature.Length > 2000 ? signature[..2000] : signature,
            ReceivedAt = clock.GetUtcNow(),
            Status = BankEventStatus.Pending
        });
        try
        {
            await db.SaveChangesAsync(http.RequestAborted);
        }
        catch (DbUpdateException e) when (e.InnerException is SqlException { Number: 2601 or 2627 })
        {
            // A redelivery of an event already stored.
        }
        return Results.Ok(new { received = true });
    }

    /// <summary>Standard Webhooks verification. Constant-time; any one valid signature is enough (Meow sends several while rotating secrets).</summary>
    public static bool Verify(string secret, string id, string timestamp, string signatures, string body, DateTimeOffset now)
    {
        if (secret.Length == 0 || id.Length == 0 || signatures.Length == 0
            || !long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(seconds)).Duration() > Tolerance)
            return false;

        byte[] key;
        try
        {
            key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret);
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}"));
        foreach (var entry in signatures.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (entry.Split(',', 2) is not ["v1", var value])
                continue;
            Span<byte> given = stackalloc byte[64];
            if (Convert.TryFromBase64String(value, given, out var length) && length == expected.Length
                && CryptographicOperations.FixedTimeEquals(given[..length], expected))
                return true;
        }
        return false;
    }

    /// <summary>Signs as Meow does; for the sandbox's simulated events and for tests.</summary>
    public static string Sign(string secret, string id, string timestamp, string body)
    {
        var key = Convert.FromBase64String(secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret);
        return "v1," + Convert.ToBase64String(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"{id}.{timestamp}.{body}")));
    }
}

/// <summary>
/// Applies stored bank events, sends approved payments that are due (first tries and retries while
/// the bank was down), and expires approvals that ran out of time.
/// </summary>
public sealed class BankingWorker(IServiceScopeFactory scopes, IOptions<PaymentsOptions> options, TimeProvider clock, ILogger<BankingWorker> log) : BackgroundService
{
    const int MaxEventAttempts = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Banking.Enabled)
            return;
        using var timer = new PeriodicTimer(options.Value.Banking.WorkerInterval, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Banking worker pass failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var banking = scope.ServiceProvider.GetRequiredService<BankingService>();

        foreach (var e in await db.BankWebhookEvents.Where(e => e.Status == BankEventStatus.Pending).OrderBy(e => e.Id).Take(50).ToListAsync(ct))
        {
            try
            {
                await banking.ApplyEventAsync(e, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning(ex, "Bank event {EventId} ({Type}) failed", e.EventId, e.EventType);
                db.ChangeTracker.Clear();
                var row = await db.BankWebhookEvents.SingleAsync(x => x.Id == e.Id, ct);
                row.Attempts++;
                row.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                if (row.Attempts >= MaxEventAttempts)
                    row.Status = BankEventStatus.Failed;
                await db.SaveChangesAsync(ct);
            }
        }

        var now = clock.GetUtcNow();
        foreach (var id in await db.TreasuryPayments.Where(p => p.Status == PaymentStatus.Approved && p.NextDispatchAt <= now)
                     .OrderBy(p => p.NextDispatchAt).Select(p => p.Id).Take(20).ToListAsync(ct))
        {
            db.ChangeTracker.Clear();
            var payment = await db.TreasuryPayments.SingleAsync(p => p.Id == id, ct);
            await banking.DispatchAsync(payment, ct);
        }

        db.ChangeTracker.Clear();
        await banking.ExpireApprovalsAsync(ct);
    }
}
