using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace NdeipiChat.Tests.Infrastructure;

/// <summary>The Enterprise Server's banking API: records what the gateway forwards and answers from a script.</summary>
public sealed class StubTreasury : HttpMessageHandler
{
    public const string GatewayKey = "nd_op_gateway-test-key";

    public sealed record Seen(HttpMethod Method, string PathAndQuery, IReadOnlyDictionary<string, string> Headers, string? Body);

    readonly ConcurrentQueue<Seen> _seen = new();

    public IReadOnlyList<Seen> Requests => [.. _seen];

    /// <summary>The answer to everything, unless a test sets another.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage> Answer { get; set; } =
        _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") };

    /// <summary>
    /// For the local preview: passes everything to a real payments server (one run with the simulated
    /// bank), with that server's gateway key in place of the test one.
    /// </summary>
    public void ForwardTo(Uri baseUrl, string gatewayKey)
    {
        var http = new HttpClient { BaseAddress = baseUrl };
        Answer = request =>
        {
            var forwarded = new HttpRequestMessage(request.Method, request.RequestUri!.PathAndQuery.TrimStart('/'));
            foreach (var header in request.Headers.Where(h => h.Key != "Operator-Key"))
                forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            forwarded.Headers.Add("Operator-Key", gatewayKey);
            if (request.Content is not null)
            {
                forwarded.Content = new ByteArrayContent(request.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult());
                foreach (var header in request.Content.Headers)
                    forwarded.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
            return http.Send(forwarded);
        };
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        _seen.Enqueue(new Seen(request.Method, request.RequestUri!.PathAndQuery, headers, body));
        return Answer(request);
    }
}
