using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests;

/// <summary>User accounts (SRS §4.1), the first slice through the foundation.</summary>
public sealed class UsersTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    [Fact]
    public async Task Creating_a_user_returns_it_not_started_with_snake_case_fields()
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.PostAsync("/v1/users", new
        {
            type = "individual",
            external_reference = "crm-1042",
            email = "alice@example.com",
            first_name = "Alice",
            last_name = "Moyo",
            country = "ZW",
            metadata = new Dictionary<string, string> { ["tier"] = "gold" }
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var user = await response.JsonAsync();
        Assert.StartsWith("usr_", user.GetProperty("id").GetString());
        Assert.Equal(30, user.GetProperty("id").GetString()!.Length);
        Assert.Equal("user", user.GetProperty("object").GetString());
        Assert.Equal("individual", user.GetProperty("type").GetString());
        Assert.Equal("crm-1042", user.GetProperty("external_reference").GetString());
        Assert.Equal("active", user.GetProperty("status").GetString());
        Assert.Equal("not_started", user.GetProperty("kyc_status").GetString());
        Assert.Equal("pending", user.GetProperty("terms_status").GetString());
        Assert.Equal(0, user.GetProperty("rejection_reasons").GetArrayLength());
        Assert.Equal("gold", user.GetProperty("metadata").GetProperty("tier").GetString());
        Assert.Equal(1, user.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task A_second_user_with_the_same_external_reference_is_a_conflict_naming_the_first()
    {
        var acme = await app.CreateIntegratorAsync();
        var first = await (await acme.PostAsync("/v1/users", new { type = "individual", external_reference = "crm-7" })).JsonAsync();

        var response = await acme.PostAsync("/v1/users", new { type = "business", external_reference = "crm-7", business_name = "Moyo Farms" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var error = await response.JsonAsync();
        Assert.Equal("external_reference_exists", error.GetProperty("code").GetString());
        Assert.Equal("external_reference", error.GetProperty("field").GetString());
        Assert.Equal(first.GetProperty("id").GetString(), error.GetProperty("existing_id").GetString());
    }

    [Fact]
    public async Task Invalid_fields_are_reported_one_by_one()
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.PostAsync("/v1/users", new { type = "business", external_reference = "crm-8", phone = "0771234567", country = "zw" });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var error = await response.JsonAsync();
        Assert.Equal("validation_failed", error.GetProperty("code").GetString());
        var fields = error.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("field").GetString()).ToList();
        Assert.Equal(new[] { "business_name", "country", "phone" }, fields.Order());
    }

    [Fact]
    public async Task An_unknown_user_type_is_a_bad_request()
    {
        var acme = await app.CreateIntegratorAsync();

        var response = await acme.PostAsync("/v1/users", new { type = "robot", external_reference = "crm-9" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalid_request", await response.ErrorCodeAsync());
    }

    [Fact]
    public async Task Creating_a_user_writes_its_event_in_the_same_transaction()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await (await acme.PostAsync("/v1/users", new { type = "individual", external_reference = "crm-10" })).JsonAsync();
        var id = user.GetProperty("id").GetString()!;

        var events = await app.DbAsync(db => db.Events.IgnoreQueryFilters().Where(e => e.ObjectId == id).ToListAsync());

        var created = Assert.Single(events);
        Assert.Equal(EventTypes.UserCreated, created.Type);
        Assert.Equal(acme.Id, created.IntegratorId);
        Assert.Equal(1, created.ObjectVersion);
        Assert.Equal(id, JsonDocument.Parse(created.DataJson).RootElement.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Users_list_newest_first_and_page_by_cursor_in_both_directions()
    {
        var acme = await app.CreateIntegratorAsync();
        var ids = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var user = await (await acme.PostAsync("/v1/users", new { type = "individual", external_reference = $"page-{i}" })).JsonAsync();
            ids.Add(user.GetProperty("id").GetString()!);
            await Task.Delay(2); // distinct milliseconds, so ULID order is creation order
        }
        ids.Reverse();

        var first = await (await acme.GetAsync("/v1/users?limit=2")).JsonAsync();
        var second = await (await acme.GetAsync($"/v1/users?limit=2&starting_after={ids[1]}")).JsonAsync();
        var back = await (await acme.GetAsync($"/v1/users?limit=2&ending_before={ids[2]}")).JsonAsync();

        Assert.Equal(ids[..2], Ids(first));
        Assert.True(first.GetProperty("has_more").GetBoolean());
        Assert.Equal(ids[2..4], Ids(second));
        Assert.True(second.GetProperty("has_more").GetBoolean());
        Assert.Equal(ids[..2], Ids(back));
        Assert.False(back.GetProperty("has_more").GetBoolean());

        var bad = await acme.GetAsync("/v1/users?limit=101");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("limit", (await bad.JsonAsync()).GetProperty("field").GetString());

        static List<string> Ids(JsonElement page) => [.. page.GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetString()!)];
    }

    [Fact]
    public async Task Users_can_be_filtered_by_kyc_status_using_the_wire_spelling()
    {
        var acme = await app.CreateIntegratorAsync();
        await acme.PostAsync("/v1/users", new { type = "individual", external_reference = "filter-1" });

        var notStarted = await (await acme.GetAsync("/v1/users?kyc_status=not_started")).JsonAsync();
        var approved = await (await acme.GetAsync("/v1/users?kyc_status=approved")).JsonAsync();
        var bad = await acme.GetAsync("/v1/users?kyc_status=maybe");

        Assert.Equal(1, notStarted.GetProperty("data").GetArrayLength());
        Assert.Equal(0, approved.GetProperty("data").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Updating_a_user_changes_contact_details_and_bumps_the_version()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await (await acme.PostAsync("/v1/users", new { type = "individual", external_reference = "upd-1" })).JsonAsync();
        var id = user.GetProperty("id").GetString();

        var response = await acme.PatchAsync($"/v1/users/{id}", new { email = "new@example.com", phone = "+263771234567" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var updated = await response.JsonAsync();
        Assert.Equal("new@example.com", updated.GetProperty("email").GetString());
        Assert.Equal("+263771234567", updated.GetProperty("phone").GetString());
        Assert.Equal(2, updated.GetProperty("version").GetInt32());
    }
}
