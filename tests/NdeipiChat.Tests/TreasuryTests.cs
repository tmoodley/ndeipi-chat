using System.Net;
using System.Net.Http.Json;
using System.Text;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Business Banking's gateway (SRS-NDE-MEOW-001 §2.2): only treasury officers get through, each
/// request reaches the Enterprise Server naming the officer, and nothing outside the banking API's
/// allowlist is forwarded.
/// </summary>
public sealed class TreasuryTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task Someone_without_the_treasury_role_neither_sees_nor_reaches_business_banking()
    {
        var thabo = await app.CreateUserAsync("Thabo");
        var before = app.Treasury.Requests.Count;

        var manifest = await thabo.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);
        var response = await thabo.Http.GetAsync("api/treasury/accounts");

        Assert.DoesNotContain(manifest.Apps, a => a.Id == TreasuryContract.AppId);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(before, app.Treasury.Requests.Count);
    }

    [Fact]
    public async Task An_officers_request_is_forwarded_with_the_gateway_key_their_identity_and_its_idempotency_key()
    {
        var thandi = await app.CreateUserAsync("Thandi Moyo", roles: ["treasury"]);
        app.Treasury.Answer = _ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"id":"tp_1","status":"pending_approval"}""", Encoding.UTF8, "application/json")
        };

        var manifest = await thandi.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);
        var request = new HttpRequestMessage(HttpMethod.Post, "api/treasury/payments")
        {
            Content = new StringContent("""{"rail":"wire","amount":"25000.00"}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "key-123");
        var response = await thandi.Http.SendAsync(request);
        var seen = app.Treasury.Requests[^1];

        Assert.Contains(manifest.Apps, a => a.Id == TreasuryContract.AppId);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("""{"id":"tp_1","status":"pending_approval"}""", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("/ops/v1/banking/payments", seen.PathAndQuery);
        Assert.Equal(StubTreasury.GatewayKey, seen.Headers["Operator-Key"]);
        Assert.Equal($"{thandi.Id:N};Thandi%20Moyo", seen.Headers["Acting-Officer"]);
        Assert.Equal("key-123", seen.Headers["Idempotency-Key"]);
        Assert.Equal("""{"rail":"wire","amount":"25000.00"}""", seen.Body);
    }

    [Fact]
    public async Task Queries_pass_through_and_paths_outside_the_banking_api_are_refused()
    {
        var thandi = await app.CreateUserAsync("Thandi Moyo", roles: ["treasury"]);
        app.Treasury.Answer = _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"data":[]}""", Encoding.UTF8, "application/json") };
        var before = app.Treasury.Requests.Count;

        var listed = await thandi.Http.GetAsync("api/treasury/payments?status=pending_approval&limit=200");
        var outside = await thandi.Http.GetAsync("api/treasury/reviews");
        var climbing = await thandi.Http.GetAsync("api/treasury/payees/%2E%2E/%2E%2E/reviews");
        var refunds = await thandi.Http.PostAsJsonAsync("api/treasury/payments/tp_1/refund", new { });

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        Assert.Equal("/ops/v1/banking/payments?status=pending_approval&limit=200", app.Treasury.Requests[^1].PathAndQuery);
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, climbing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, refunds.StatusCode);
        Assert.Equal(before + 1, app.Treasury.Requests.Count);
    }

    [Fact]
    public async Task The_server_s_answer_keeps_its_status_and_no_store_header()
    {
        var thandi = await app.CreateUserAsync("Thandi Moyo", roles: ["treasury"]);
        app.Treasury.Answer = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent("""{"code":"already_revealed","message":"Shown once already."}""", Encoding.UTF8, "application/json")
            };
            response.Headers.CacheControl = new() { NoStore = true };
            return response;
        };

        var response = await thandi.Http.PostAsJsonAsync("api/treasury/cards/card_1/number", new { });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("already_revealed", await response.Content.ReadAsStringAsync());
    }
}
