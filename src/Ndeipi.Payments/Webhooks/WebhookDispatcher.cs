using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Webhooks;

/// <summary>
/// Sends event deliveries (FR-WH-02, FR-WH-04). Every attempt is signed at its own time with the
/// endpoint's key (<see cref="WebhookSigner"/>), so a retry two days later still passes the
/// receiver's 10-minute tolerance. A 2xx answer within the timeout is success; anything else is
/// retried with exponential backoff and jitter until two days after the delivery was created.
///
/// Deliveries are claimed with a lease before sending, so several server instances can run the
/// dispatcher at once without sending the same delivery twice at the same moment. A receiver may
/// still see a delivery again after a crash mid-send; that is why events carry IDs to deduplicate on.
/// </summary>
public sealed class WebhookDispatcher(
    IServiceScopeFactory scopes,
    IHttpClientFactory http,
    IDataProtectionProvider protection,
    IOptions<PaymentsOptions> options,
    TimeProvider clock,
    ILogger<WebhookDispatcher> log) : BackgroundService
{
    public const string EventIdHeader = "Ndeipi-Event-Id";
    public const string AttemptHeader = "Ndeipi-Delivery-Attempt";

    static readonly TimeSpan Lease = TimeSpan.FromMinutes(1);

    PaymentsOptions.WebhookOptions Settings => options.Value.Webhooks;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (await DispatchDueAsync(stoppingToken) == 0)
                    await Task.Delay(Settings.PollInterval, clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                log.LogError(e, "Webhook dispatch pass failed.");
                await Task.Delay(Settings.PollInterval, clock, stoppingToken);
            }
        }
    }

    /// <summary>Sends every delivery that is due, up to a batch; returns how many it sent.</summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        List<long> due;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var now = clock.GetUtcNow();
            due = await scope.ServiceProvider.GetRequiredService<PaymentsDbContext>().EventDeliveries.IgnoreQueryFilters()
                .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAt <= now && (d.LockedUntil == null || d.LockedUntil < now))
                .OrderBy(d => d.NextAttemptAt)
                .Select(d => d.Id)
                .Take(Settings.BatchSize)
                .ToListAsync(ct);
        }

        var sent = 0;
        await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = ct }, async (id, token) =>
        {
            if (await DeliverAsync(id, token))
                Interlocked.Increment(ref sent);
        });
        return sent;
    }

    async Task<bool> DeliverAsync(long id, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PaymentsDbContext>();
        var now = clock.GetUtcNow();

        // Claim it; another instance may have got there first.
        var claimed = await db.EventDeliveries.IgnoreQueryFilters()
            .Where(d => d.Id == id && d.Status == DeliveryStatus.Pending && (d.LockedUntil == null || d.LockedUntil < now))
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.LockedUntil, now + Lease), ct);
        if (claimed == 0)
            return false;

        var delivery = await db.EventDeliveries.IgnoreQueryFilters().SingleAsync(d => d.Id == id, ct);
        var endpoint = await db.WebhookEndpoints.IgnoreQueryFilters().SingleAsync(w => w.Id == delivery.WebhookEndpointId, ct);
        var record = await db.Events.IgnoreQueryFilters().AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
        delivery.LockedUntil = null;

        if (endpoint.DeletedAt is not null)
        {
            delivery.Status = DeliveryStatus.Canceled;
            delivery.LastError = "The endpoint was deleted.";
        }
        else if (endpoint.Status == WebhookEndpointStatus.Disabled)
        {
            // Held, not attempted, while the integrator has the endpoint switched off.
            Reschedule(delivery, now, Settings.RetryBase);
        }
        else
            await AttemptAsync(delivery, endpoint, record, now, ct);

        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    async Task AttemptAsync(EventDelivery delivery, WebhookEndpoint endpoint, EventRecord record, DateTimeOffset now, CancellationToken ct)
    {
        delivery.Attempts++;
        delivery.LastAttemptAt = now;

        var body = JsonSerializer.SerializeToUtf8Bytes(EventDto.From(record), PaymentsJson.Options);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url) { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Headers.Add(WebhookSigner.Header, WebhookSigner.Sign(body, now, WebhookEndpointService.PrivateKey(endpoint, protection)));
        request.Headers.Add(EventIdHeader, record.Id);
        request.Headers.Add(AttemptHeader, delivery.Attempts.ToString());

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Settings.Timeout);
        try
        {
            using var response = await http.CreateClient(WebhookHttp.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            delivery.LastResponseStatus = (int)response.StatusCode;
            delivery.LastError = null;
            if (response.IsSuccessStatusCode)
            {
                delivery.Status = DeliveryStatus.Succeeded;
                return;
            }
        }
        catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            delivery.LastResponseStatus = null;
            delivery.LastError = e is OperationCanceledException ? $"No response within {Settings.Timeout.TotalSeconds:0} seconds." : Truncate(e.Message);
        }

        Reschedule(delivery, now, Backoff(delivery.Attempts));
    }

    void Reschedule(EventDelivery delivery, DateTimeOffset now, TimeSpan delay)
    {
        if (now + delay > delivery.GiveUpAt)
        {
            delivery.Status = DeliveryStatus.Failed;
            return;
        }
        delivery.NextAttemptAt = now + delay;
    }

    /// <summary>RetryBase doubled per attempt, capped, with ±20% jitter so retries from one outage spread out.</summary>
    TimeSpan Backoff(int attempts)
    {
        var delay = Math.Min(Settings.RetryBase.TotalMilliseconds * Math.Pow(2, attempts - 1), Settings.MaxRetryDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(delay * (0.8 + Random.Shared.NextDouble() * 0.4));
    }

    static string Truncate(string message) => message.Length > 500 ? message[..500] : message;
}
