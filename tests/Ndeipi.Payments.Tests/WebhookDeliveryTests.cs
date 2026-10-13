using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// Webhook endpoints and delivery (SRS §4.9): registration (FR-WH-01), signed deliveries checked
/// with the endpoint's public key (FR-WH-02, FR-WH-03, FR-WH-05), retries with backoff and giving
/// up (FR-WH-04), subscriptions, deletion, the events API and redelivery (SRV-OPS-05), and
/// integrator isolation.
/// </summary>
public sealed class WebhookDeliveryTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    /// <summary>An enabled endpoint for <paramref name="url"/>, and its public key.</summary>
    async Task<(string Id, string PublicKey)> EndpointAsync(TestIntegrator integrator, string url, params string[] types)
    {
        var created = await (await integrator.PostAsync("/v1/webhook_endpoints", new { url, event_types = types })).JsonAsync();
        var id = created.GetProperty("id").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await integrator.PatchAsync($"/v1/webhook_endpoints/{id}", new { status = "enabled" })).StatusCode);
        return (id, created.GetProperty("public_key_pem").GetString()!);
    }

    static Task<HttpResponseMessage> CreateUserAsync(TestIntegrator integrator) =>
        integrator.PostAsync("/v1/users", new { type = "individual", external_reference = $"crm-{Guid.NewGuid():N}" });

    static async Task<string> UserIdAsync(HttpResponseMessage created) => (await created.JsonAsync()).GetProperty("id").GetString()!;

    Task<EventDelivery> DeliveryAsync(string endpointId) =>
        app.DbAsync(db => db.EventDeliveries.IgnoreQueryFilters().SingleAsync(d => d.WebhookEndpointId == endpointId));

    async Task<EventDelivery> WaitForDeliveryAsync(string endpointId, DeliveryStatus status)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var delivery = await DeliveryAsync(endpointId);
            if (delivery.Status == status)
                return delivery;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Delivery stayed {delivery.Status} after {delivery.Attempts} attempts.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task A_new_endpoint_starts_disabled_with_its_own_public_key()
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.PostAsync("/v1/webhook_endpoints", new
        {
            url = WebhookReceiver.NewUrl(),
            event_types = new[] { EventTypes.UserCreated, EventTypes.UserCreated, EventTypes.TransferStateChanged },
            description = "Production listener"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var endpoint = await response.JsonAsync();
        Assert.StartsWith("we_", endpoint.GetProperty("id").GetString());
        Assert.Equal("disabled", endpoint.GetProperty("status").GetString());
        Assert.Equal(new[] { EventTypes.TransferStateChanged, EventTypes.UserCreated }, endpoint.GetProperty("event_types").EnumerateArray().Select(t => t.GetString()));
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", endpoint.GetProperty("public_key_pem").GetString());

        var stored = await app.DbAsync(db => db.WebhookEndpoints.IgnoreQueryFilters().SingleAsync(w => w.Id == endpoint.GetProperty("id").GetString()));
        Assert.DoesNotContain("PRIVATE", stored.ProtectedPrivateKey);
    }

    [Theory]
    [InlineData("http://hooks.integrator.test/plain", "url")]
    [InlineData("https://user:secret@hooks.integrator.test/", "url")]
    [InlineData("not a url", "url")]
    public async Task Endpoints_must_be_https_without_credentials(string url, string field)
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.PostAsync("/v1/webhook_endpoints", new { url });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(field, (await response.JsonAsync()).GetProperty("field").GetString());
    }

    [Fact]
    public async Task Unknown_event_types_are_refused()
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.PostAsync("/v1/webhook_endpoints", new { url = WebhookReceiver.NewUrl(), event_types = new[] { "user.exploded" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("unknown_event_type", (await response.JsonAsync()).GetProperty("details")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task An_event_is_delivered_signed_and_verifies_with_the_endpoints_public_key()
    {
        var acme = await app.CreateIntegratorAsync();
        var url = WebhookReceiver.NewUrl();
        var (_, publicKey) = await EndpointAsync(acme, url);

        var user = await UserIdAsync(await CreateUserAsync(acme));
        var received = (await app.Webhooks.WaitForAsync(url)).Single();

        Assert.True(WebhookSigner.Verify(received.Body, received.Signature, publicKey, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10)));
        Assert.Equal(1, received.Attempt);
        var body = received.Json;
        Assert.Equal(received.EventId, body.GetProperty("id").GetString());
        Assert.Equal("event", body.GetProperty("object").GetString());
        Assert.Equal(EventTypes.UserCreated, body.GetProperty("type").GetString());
        Assert.Equal("v1", body.GetProperty("api_version").GetString());
        Assert.Equal(user, body.GetProperty("data").GetProperty("id").GetString());
        Assert.Equal(1, body.GetProperty("object_version").GetInt32());
    }

    [Fact]
    public async Task A_failing_endpoint_is_retried_and_each_attempt_is_signed_afresh()
    {
        var acme = await app.CreateIntegratorAsync();
        var url = WebhookReceiver.NewUrl();
        app.Webhooks.Script(url, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable);
        var (id, publicKey) = await EndpointAsync(acme, url);

        await CreateUserAsync(acme);
        var attempts = await app.Webhooks.WaitForAsync(url, count: 3);
        var delivery = await WaitForDeliveryAsync(id, DeliveryStatus.Succeeded);

        Assert.Equal(new[] { 1, 2, 3 }, attempts.Select(a => a.Attempt));
        Assert.All(attempts, a => Assert.True(WebhookSigner.Verify(a.Body, a.Signature, publicKey, a.At, TimeSpan.FromMinutes(10))));
        Assert.Single(attempts.Select(a => a.EventId).Distinct());
        Assert.Equal(3, delivery.Attempts);
        Assert.Equal(200, delivery.LastResponseStatus);
    }

    [Fact]
    public async Task Retrying_stops_when_the_window_closes()
    {
        var acme = await app.CreateIntegratorAsync();
        var url = WebhookReceiver.NewUrl();
        app.Webhooks.AlwaysAnswer(url, HttpStatusCode.InternalServerError);
        var (id, _) = await EndpointAsync(acme, url);

        await CreateUserAsync(acme);
        var delivery = await WaitForDeliveryAsync(id, DeliveryStatus.Failed);

        Assert.True(delivery.Attempts >= 3, $"Only {delivery.Attempts} attempts before giving up.");
        Assert.Equal(500, delivery.LastResponseStatus);
    }

    [Fact]
    public async Task An_endpoint_only_gets_the_types_it_subscribed_to()
    {
        var acme = await app.CreateIntegratorAsync();
        var url = WebhookReceiver.NewUrl();
        await EndpointAsync(acme, url, EventTypes.UserKycStatusChanged);

        var user = await UserIdAsync(await CreateUserAsync(acme));
        await acme.PostAsync($"/v1/sandbox/users/{user}/kyc", new { kyc_status = "under_review" });
        var received = await app.Webhooks.WaitForAsync(url);

        Assert.All(received, r => Assert.Equal(EventTypes.UserKycStatusChanged, r.Json.GetProperty("type").GetString()));
    }

    [Fact]
    public async Task A_disabled_endpoint_is_not_sent_new_events()
    {
        var acme = await app.CreateIntegratorAsync();
        var url = WebhookReceiver.NewUrl();
        await acme.PostAsync("/v1/webhook_endpoints", new { url });

        await CreateUserAsync(acme);
        await Task.Delay(300);

        Assert.Empty(app.Webhooks.At(url));
    }

    [Fact]
    public async Task Deleting_an_endpoint_cancels_what_it_has_not_yet_accepted()
    {
        var acme = await app.CreateIntegratorAsync();
        var url = WebhookReceiver.NewUrl();
        app.Webhooks.AlwaysAnswer(url, HttpStatusCode.BadGateway);
        var (id, _) = await EndpointAsync(acme, url);
        await CreateUserAsync(acme);
        await app.Webhooks.WaitForAsync(url);

        var deleted = await acme.Client.DeleteAsync($"/v1/webhook_endpoints/{id}");
        var delivery = await WaitForDeliveryAsync(id, DeliveryStatus.Canceled);

        Assert.Equal(HttpStatusCode.OK, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await acme.GetAsync($"/v1/webhook_endpoints/{id}")).StatusCode);
        Assert.Contains("deleted", delivery.LastError);
    }

    [Fact]
    public async Task Events_can_be_listed_fetched_and_redelivered()
    {
        var acme = await app.CreateIntegratorAsync();
        var url = WebhookReceiver.NewUrl();
        var (id, _) = await EndpointAsync(acme, url);
        var user = await UserIdAsync(await CreateUserAsync(acme));
        var first = (await app.Webhooks.WaitForAsync(url)).Single();

        var listed = await (await acme.GetAsync($"/v1/events?type={EventTypes.UserCreated}&object_id={user}")).JsonAsync();
        var eventId = listed.GetProperty("data")[0].GetProperty("id").GetString();
        var fetched = await acme.GetAsync($"/v1/events/{eventId}");
        var redeliver = await acme.PostAsync($"/v1/events/{eventId}/redeliver", new { webhook_endpoint_id = id });
        var deliveries = await app.Webhooks.WaitForAsync(url, count: 2);

        Assert.Equal(first.EventId, eventId);
        Assert.Equal(HttpStatusCode.OK, fetched.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, redeliver.StatusCode);
        Assert.Equal(new[] { 1, 1 }, deliveries.Select(d => d.Attempt));
        Assert.All(deliveries, d => Assert.Equal(eventId, d.EventId));
    }

    [Fact]
    public async Task One_integrator_never_sees_or_receives_anothers_events()
    {
        var acme = await app.CreateIntegratorAsync("Acme");
        var zed = await app.CreateIntegratorAsync("Zed");
        var zedUrl = WebhookReceiver.NewUrl();
        await EndpointAsync(zed, zedUrl);

        var user = await UserIdAsync(await CreateUserAsync(acme));
        await Task.Delay(300);
        var acmeEvent = (await (await acme.GetAsync($"/v1/events?object_id={user}")).JsonAsync()).GetProperty("data")[0].GetProperty("id").GetString();

        Assert.Empty(app.Webhooks.At(zedUrl));
        Assert.Equal(0, (await (await zed.GetAsync($"/v1/events?object_id={user}")).JsonAsync()).GetProperty("data").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await zed.GetAsync($"/v1/events/{acmeEvent}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await zed.PostAsync($"/v1/events/{acmeEvent}/redeliver", null)).StatusCode);
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Deliveries_only_connect_to_public_addresses(string address, bool allowed) =>
        Assert.Equal(allowed, WebhookHttp.IsPublic(IPAddress.Parse(address)));
}
