using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests.Infrastructure;

/// <summary>One delivery as an integrator's endpoint received it.</summary>
public sealed record ReceivedDelivery(Uri Url, string? Signature, string? EventId, int Attempt, byte[] Body, DateTimeOffset At)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

/// <summary>
/// Stands in for every integrator's webhook endpoint: the dispatcher's HTTP client sends here. Each
/// test uses its own URL and can script what that URL answers.
/// </summary>
public sealed class WebhookReceiver : HttpMessageHandler
{
    readonly ConcurrentQueue<ReceivedDelivery> _received = new();
    readonly ConcurrentDictionary<string, ConcurrentQueue<HttpStatusCode>> _scripts = new();

    public static string NewUrl() => $"https://hooks.integrator.test/{Guid.NewGuid():N}";

    /// <summary>Answers these statuses in turn, then 200.</summary>
    public void Script(string url, params HttpStatusCode[] statuses) => _scripts[url] = new ConcurrentQueue<HttpStatusCode>(statuses);

    /// <summary>Always answers <paramref name="status"/>.</summary>
    public void AlwaysAnswer(string url, HttpStatusCode status) =>
        _scripts[url] = new ConcurrentQueue<HttpStatusCode>(Enumerable.Repeat(status, 10_000));

    public IReadOnlyList<ReceivedDelivery> At(string url) => [.. _received.Where(r => r.Url.ToString() == url)];

    /// <summary>Waits until <paramref name="url"/> has received <paramref name="count"/> deliveries matching <paramref name="match"/>.</summary>
    public async Task<IReadOnlyList<ReceivedDelivery>> WaitForAsync(string url, int count = 1, Func<ReceivedDelivery, bool>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var got = At(url).Where(match ?? (_ => true)).ToList();
            if (got.Count >= count)
                return got;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"{url} received {got.Count} of {count} expected deliveries.");
            await Task.Delay(25);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
        var url = request.RequestUri!.ToString();
        _received.Enqueue(new ReceivedDelivery(
            request.RequestUri!,
            request.Headers.TryGetValues(WebhookSigner.Header, out var signature) ? signature.Single() : null,
            request.Headers.TryGetValues(WebhookDispatcher.EventIdHeader, out var id) ? id.Single() : null,
            request.Headers.TryGetValues(WebhookDispatcher.AttemptHeader, out var attempt) ? int.Parse(attempt.Single()) : 0,
            body,
            DateTimeOffset.UtcNow));

        var status = _scripts.TryGetValue(url, out var script) && script.TryDequeue(out var next) ? next : HttpStatusCode.OK;
        return new HttpResponseMessage(status);
    }
}
