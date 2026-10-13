using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Tests.Infrastructure;
using Ndeipi.Payments.Users;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Tests;

/// <summary>
/// Hosted onboarding links (FR-USER-03), KYC and terms statuses with their reasons and events
/// (FR-USER-04, FR-USER-06, FR-USER-08), and the sandbox KYC call (FR-SBX-02).
/// </summary>
public sealed class OnboardingTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    static async Task<string> CreateUserAsync(TestIntegrator integrator, string type = "individual") =>
        (await (await integrator.PostAsync("/v1/users", new { type, external_reference = $"crm-{Guid.NewGuid():N}", business_name = type == "business" ? "Moyo Farms" : null }))
            .JsonAsync()).GetProperty("id").GetString()!;

    [Fact]
    public async Task One_call_issues_a_kyc_link_and_a_terms_link_with_their_statuses()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);

        var response = await acme.PostAsync($"/v1/users/{user}/onboarding_links", new { redirect_url = "https://app.acme.test/done" });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var links = await response.JsonAsync();
        Assert.Equal("onboarding_links", links.GetProperty("object").GetString());
        Assert.Equal(user, links.GetProperty("user_id").GetString());
        Assert.Contains(user, links.GetProperty("kyc").GetProperty("url").GetString());
        Assert.Equal("not_started", links.GetProperty("kyc").GetProperty("status").GetString());
        Assert.StartsWith("https://onboarding.sandbox.ndeipi.example/terms/", links.GetProperty("terms").GetProperty("url").GetString());
        Assert.Equal("pending", links.GetProperty("terms").GetProperty("status").GetString());
        Assert.True(links.GetProperty("terms").GetProperty("expires_at").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddDays(6));
    }

    [Fact]
    public async Task The_body_is_optional_and_new_links_supersede_the_old_ones()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);

        Assert.Equal(HttpStatusCode.Created, (await acme.PostAsync($"/v1/users/{user}/onboarding_links", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await acme.PostAsync($"/v1/users/{user}/onboarding_links", null)).StatusCode);

        var links = await app.DbAsync(db => db.OnboardingLinks.IgnoreQueryFilters().Where(l => l.UserId == user).ToListAsync());
        Assert.Equal(4, links.Count);
        Assert.Equal(2, links.Count(l => l.SupersededAt is null));
    }

    [Fact]
    public async Task The_terms_link_names_its_user_and_cannot_be_forged()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);
        var links = await (await acme.PostAsync($"/v1/users/{user}/onboarding_links", null)).JsonAsync();
        var token = Uri.UnescapeDataString(links.GetProperty("terms").GetProperty("url").GetString()!.Split('/')[^1]);

        var (named, forged) = await app.AsIntegratorAsync(acme.Id, sp =>
        {
            var onboarding = sp.GetRequiredService<OnboardingService>();
            return Task.FromResult((onboarding.UserForTermsToken(token), onboarding.UserForTermsToken(token[..^4] + "AAAA")));
        });

        Assert.Equal(user, named);
        Assert.Null(forged);
    }

    [Fact]
    public async Task A_redirect_url_must_be_https_and_the_user_must_exist()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);

        var http = await acme.PostAsync($"/v1/users/{user}/onboarding_links", new { redirect_url = "http://app.acme.test/done" });
        var missing = await acme.PostAsync("/v1/users/usr_01M495MISSINGUSER0000000000/onboarding_links", null);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, http.StatusCode);
        Assert.Equal("redirect_url", (await http.JsonAsync()).GetProperty("field").GetString());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Approving_kyc_in_the_sandbox_approves_terms_and_emits_one_event_for_each()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);

        var response = await acme.PostAsync($"/v1/sandbox/users/{user}/kyc", new { kyc_status = "approved" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var approved = await response.JsonAsync();
        Assert.Equal("approved", approved.GetProperty("kyc_status").GetString());
        Assert.Equal("approved", approved.GetProperty("terms_status").GetString());
        Assert.Equal(2, approved.GetProperty("version").GetInt32());

        var events = await (await acme.GetAsync($"/v1/events?object_id={user}")).JsonAsync();
        var types = events.GetProperty("data").EnumerateArray().Select(e => e.GetProperty("type").GetString()).ToList();
        Assert.Equal(new[] { EventTypes.UserCreated, EventTypes.UserKycStatusChanged, EventTypes.UserTermsStatusChanged }, types.Order());

        var kycEvent = events.GetProperty("data").EnumerateArray().Single(e => e.GetProperty("type").GetString() == EventTypes.UserKycStatusChanged);
        Assert.Equal("not_started", kycEvent.GetProperty("previous_attributes").GetProperty("kyc_status").GetString());
        Assert.Equal("approved", kycEvent.GetProperty("data").GetProperty("kyc_status").GetString());
        Assert.Equal(2, kycEvent.GetProperty("object_version").GetInt32());
    }

    [Fact]
    public async Task A_rejected_user_always_carries_reasons()
    {
        var acme = await app.CreateIntegratorAsync();
        var given = await CreateUserAsync(acme);
        var none = await CreateUserAsync(acme);

        var withReasons = await (await acme.PostAsync($"/v1/sandbox/users/{given}/kyc", new
        {
            kyc_status = "rejected",
            rejection_reasons = new[] { new { code = "document_expired", message = "The passport has expired." } }
        })).JsonAsync();
        var withoutReasons = await (await acme.PostAsync($"/v1/sandbox/users/{none}/kyc", new { kyc_status = "rejected" })).JsonAsync();

        Assert.Equal("document_expired", withReasons.GetProperty("rejection_reasons")[0].GetProperty("code").GetString());
        Assert.Equal("pending", withReasons.GetProperty("terms_status").GetString());
        Assert.Equal("unspecified", withoutReasons.GetProperty("rejection_reasons")[0].GetProperty("code").GetString());
    }

    [Fact]
    public async Task Setting_the_same_status_again_changes_nothing()
    {
        var acme = await app.CreateIntegratorAsync();
        var user = await CreateUserAsync(acme);
        await acme.PostAsync($"/v1/sandbox/users/{user}/kyc", new { kyc_status = "under_review" });

        var again = await (await acme.PostAsync($"/v1/sandbox/users/{user}/kyc", new { kyc_status = "under_review" })).JsonAsync();

        Assert.Equal(2, again.GetProperty("version").GetInt32());
        Assert.Equal(1, await app.DbAsync(db => db.Events.IgnoreQueryFilters().CountAsync(e => e.ObjectId == user && e.Type == EventTypes.UserKycStatusChanged)));
    }

    [Fact]
    public async Task End_to_end_steps_one_to_three_without_wallets()
    {
        // SRS §9.2 steps 1 to 3; wallets join them in M3.
        var acme = await app.CreateIntegratorAsync();
        var a = await CreateUserAsync(acme);
        var b = await CreateUserAsync(acme, "business");
        foreach (var user in new[] { a, b })
            Assert.Equal(HttpStatusCode.Created, (await acme.PostAsync($"/v1/users/{user}/onboarding_links", null)).StatusCode);

        foreach (var user in new[] { a, b })
            await acme.PostAsync($"/v1/sandbox/users/{user}/kyc", new { kyc_status = "approved" });

        var approved = await (await acme.GetAsync("/v1/users?kyc_status=approved")).JsonAsync();
        Assert.Equal(new[] { a, b }.Order(), approved.GetProperty("data").EnumerateArray().Select(u => u.GetProperty("id").GetString()!).Order());
    }
}
