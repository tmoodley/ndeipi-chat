using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Tests.Infrastructure;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// The request layer every operation runs through (milestone M1): API keys (FR-CORE-02/03,
/// SRV-OPS-01), the error body (FR-API-06/07), idempotency (FR-CORE-04/05, NFR-REL-01), request IDs
/// (NFR-OBS-01), the audit log (SRV-OPS-03) and integrator isolation (SRV-OPS-02, SC-05).
/// </summary>
public sealed class FoundationTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    static object NewUser(string? reference = null) =>
        new { type = "individual", external_reference = reference ?? $"crm-{Guid.NewGuid():N}", first_name = "Alice", last_name = "Moyo" };

    [Fact]
    public async Task A_request_without_a_key_is_refused_with_the_contract_error()
    {
        var response = await app.CreateClient().GetAsync("/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var error = await response.JsonAsync();
        Assert.Equal("unauthorized", error.GetProperty("code").GetString());
        Assert.StartsWith("req_", error.GetProperty("request_id").GetString());
        Assert.Equal(error.GetProperty("request_id").GetString(), response.Headers.GetValues(RequestIds.Header).Single());
    }

    [Fact]
    public async Task A_production_key_is_refused_by_the_sandbox_without_being_echoed()
    {
        var client = app.CreateClient();
        const string key = ApiKeyService.ProductionPrefix + "this-is-a-production-key";
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.Header, key);

        var response = await client.GetAsync("/v1/users");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("key_environment_mismatch", await response.ErrorCodeAsync());
        Assert.DoesNotContain("this-is-a-production-key", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_revoked_key_stops_working()
    {
        var acme = await app.CreateIntegratorAsync();
        Assert.Equal(HttpStatusCode.OK, (await acme.GetAsync("/v1/users")).StatusCode);

        await app.AsIntegratorAsync(acme.Id, async sp =>
        {
            await sp.GetRequiredService<ApiKeyService>().RevokeAsync(acme.KeyId, default);
            return 0;
        });

        var response = await acme.GetAsync("/v1/users");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthorized", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Keys_are_stored_only_as_hashes_and_carry_the_environment_prefix()
    {
        var acme = await app.CreateIntegratorAsync();

        Assert.StartsWith(ApiKeyService.SandboxPrefix, acme.Key);
        var stored = await app.DbAsync(db => db.ApiKeys.SingleAsync(k => k.Id == acme.KeyId));
        Assert.Equal(ApiKeyService.Hash(acme.Key), stored.KeyHash);
        Assert.Equal(acme.Key[^4..], stored.Last4);
    }

    [Fact]
    public async Task A_post_without_an_idempotency_key_is_refused()
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.Client.PostAsJsonAsync("/v1/users", NewUser());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("idempotency_key_missing", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task A_retried_post_replays_the_first_response_and_creates_nothing_new()
    {
        var acme = await app.CreateIntegratorAsync();
        var body = NewUser();
        var key = Guid.NewGuid().ToString();

        var first = await acme.PostAsync("/v1/users", body, key);
        var retry = await acme.PostAsync("/v1/users", body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, retry.StatusCode);
        Assert.Equal("true", retry.Headers.GetValues(IdempotencyMiddleware.ReplayedHeader).Single());
        Assert.False(first.Headers.Contains(IdempotencyMiddleware.ReplayedHeader));
        Assert.Equal(await first.Content.ReadAsStringAsync(), await retry.Content.ReadAsStringAsync());
        Assert.Equal(1, await app.DbAsync(db => db.Users.IgnoreQueryFilters().CountAsync(u => u.IntegratorId == acme.Id)));
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_body_is_refused()
    {
        var acme = await app.CreateIntegratorAsync();
        var key = Guid.NewGuid().ToString();
        await acme.PostAsync("/v1/users", NewUser(), key);

        var response = await acme.PostAsync("/v1/users", NewUser(), key);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("idempotency_key_reused", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Idempotency_keys_belong_to_one_integrator()
    {
        var acme = await app.CreateIntegratorAsync("Acme");
        var zed = await app.CreateIntegratorAsync("Zed");
        var key = Guid.NewGuid().ToString();

        var a = await acme.PostAsync("/v1/users", NewUser(), key);
        var z = await zed.PostAsync("/v1/users", NewUser(), key);

        Assert.Equal(HttpStatusCode.Created, a.StatusCode);
        Assert.Equal(HttpStatusCode.Created, z.StatusCode);
        Assert.False(z.Headers.Contains(IdempotencyMiddleware.ReplayedHeader));
    }

    [Fact]
    public async Task One_integrator_cannot_read_another_integrators_users()
    {
        var acme = await app.CreateIntegratorAsync("Acme");
        var zed = await app.CreateIntegratorAsync("Zed");
        var alice = await (await acme.PostAsync("/v1/users", NewUser())).JsonAsync();
        var id = alice.GetProperty("id").GetString();

        var response = await zed.GetAsync($"/v1/users/{id}");
        var list = await (await zed.GetAsync("/v1/users")).JsonAsync();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("not_found", await response.ErrorCodeAsync());
        Assert.DoesNotContain(list.GetProperty("data").EnumerateArray(), u => u.GetProperty("id").GetString() == id);
    }

    [Fact]
    public async Task Every_state_changing_call_is_audited_without_its_body()
    {
        var acme = await app.CreateIntegratorAsync();
        var key = Guid.NewGuid().ToString();

        var response = await acme.PostAsync("/v1/users", NewUser(), key);
        var requestId = response.Headers.GetValues(RequestIds.Header).Single();

        var entry = await app.DbAsync(db => db.AuditLog.SingleAsync(a => a.RequestId == requestId));
        Assert.Equal(acme.Id, entry.IntegratorId);
        Assert.Equal(acme.KeyId, entry.ApiKeyId);
        Assert.Equal("POST", entry.Method);
        Assert.Equal("/v1/users", entry.Path);
        Assert.Equal(201, entry.StatusCode);
        Assert.Equal(key, entry.IdempotencyKey);
    }

    [Fact]
    public async Task Malformed_json_and_unknown_routes_answer_with_the_contract_error()
    {
        var acme = await app.CreateIntegratorAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/users") { Content = new StringContent("{ not json", System.Text.Encoding.UTF8, "application/json") };
        request.Headers.Add(IdempotencyMiddleware.Header, Guid.NewGuid().ToString());

        var malformed = await acme.Client.SendAsync(request);
        var unknown = await acme.GetAsync("/v1/nothing-here");

        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        Assert.Equal("invalid_request", await malformed.ErrorCodeAsync());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("not_found", await unknown.ErrorCodeAsync());
    }

    [Fact]
    public async Task Operations_from_later_milestones_answer_not_implemented_and_name_the_milestone()
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.PostAsync("/v1/quotes", new { amount = "25.00" });

        Assert.Equal((HttpStatusCode)501, response.StatusCode);
        var error = await response.JsonAsync();
        Assert.Equal("not_implemented", error.GetProperty("code").GetString());
        Assert.Contains("M4", error.GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_server_failure_releases_the_idempotency_key_so_a_retry_runs_again()
    {
        var acme = await app.CreateIntegratorAsync();
        var key = Guid.NewGuid().ToString();

        // 501 stands in for any 5xx: nothing is stored, so the same key runs again rather than replaying.
        await acme.PostAsync("/v1/quotes", new { amount = "1.00" }, key);
        var retry = await acme.PostAsync("/v1/quotes", new { amount = "1.00" }, key);

        Assert.Equal((HttpStatusCode)501, retry.StatusCode);
        Assert.False(retry.Headers.Contains(IdempotencyMiddleware.ReplayedHeader));
        Assert.Equal(0, await app.DbAsync(db => db.IdempotencyRecords.IgnoreQueryFilters().CountAsync(r => r.Key == key)));
    }
}
