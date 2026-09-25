using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NdeipiChat.Api.Shamwaris;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class ShamwariTests(TestApp app) : IClassFixture<TestApp>
{
    static string NewEmail(string name) => $"{name}.{Guid.NewGuid():N}@example.test";

    /// <summary>A Zimbabwean mobile number no other test uses.</summary>
    static string NewPhone() => "+26377" + Random.Shared.Next(1_000_000, 9_999_999);

    static Task<AddShamwariResponse> AddAsync(TestUser user, string contact) =>
        user.PostAsync<AddShamwariResponse>("api/shamwaris", new AddShamwariRequest(contact));

    static async Task<ShamwariListDto> DeleteAsync(TestUser user, string path)
    {
        using var response = await user.Http.DeleteAsync(path);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ShamwariListDto>(ContractJson.Options))!;
    }

    [Fact]
    public async Task A_request_by_email_arrives_live_and_accepting_makes_both_Shamwaris()
    {
        var alice = await app.CreateUserAsync("Alice Shamwari");
        var bobEmail = NewEmail("bob");
        var bob = await app.CreateUserAsync("Bob Shamwari", bobEmail);
        await using var aliceHub = await app.ConnectAsync(alice);
        await using var bobHub = await app.ConnectAsync(bob);

        var bobNotified = Wait.ForEventAsync(bobHub, nameof(IChatClient.ShamwarisChanged));
        var added = await AddAsync(alice, "  " + bobEmail.ToUpperInvariant() + " ");
        Assert.Equal(ShamwariAddOutcome.RequestSent, added.Outcome);
        var sent = Assert.Single(added.List.Outgoing);
        Assert.Equal((bob.Id, (string?)null), (sent.User?.Id, sent.Contact));
        await bobNotified;

        var bobsList = await bob.GetAsync<ShamwariListDto>("api/shamwaris");
        var request = Assert.Single(bobsList.Incoming);
        Assert.Equal(alice.Id, request.User?.Id);
        Assert.Empty(bobsList.Shamwaris);

        var aliceNotified = Wait.ForEventAsync(aliceHub, nameof(IChatClient.ShamwarisChanged));
        var accepted = await bob.PostAsync<ShamwariListDto>($"api/shamwaris/requests/{request.Id}/accept", new { });
        Assert.Equal(alice.Id, Assert.Single(accepted.Shamwaris).User.Id);
        Assert.Empty(accepted.Incoming);
        await aliceNotified;

        var alicesList = await alice.GetAsync<ShamwariListDto>("api/shamwaris");
        Assert.Equal(bob.Id, Assert.Single(alicesList.Shamwaris).User.Id);
        Assert.Empty(alicesList.Outgoing);

        Assert.Equal(ShamwariAddOutcome.AlreadyShamwaris, (await AddAsync(alice, bobEmail)).Outcome);
    }

    [Fact]
    public async Task Phone_numbers_match_however_they_are_typed()
    {
        var alice = await app.CreateUserAsync("Alice Phone");
        var phone = NewPhone();
        var bob = await app.CreateUserAsync("Bob Phone", phone: phone);

        var typed = $"00{phone[1..4]} ({phone[4..6]}) {phone[6..9]}-{phone[9..]}";
        var added = await AddAsync(alice, typed);

        Assert.Equal(ShamwariAddOutcome.RequestSent, added.Outcome);
        Assert.Equal(bob.Id, Assert.Single(added.List.Outgoing).User?.Id);
        Assert.Equal(ShamwariAddOutcome.AlreadyRequested, (await AddAsync(alice, phone)).Outcome);
    }

    [Fact]
    public async Task Adding_someone_who_already_asked_you_accepts_their_request()
    {
        var aliceEmail = NewEmail("alice");
        var alice = await app.CreateUserAsync("Alice Mutual", aliceEmail);
        var bobEmail = NewEmail("bob");
        var bob = await app.CreateUserAsync("Bob Mutual", bobEmail);

        await AddAsync(alice, bobEmail);
        var back = await AddAsync(bob, aliceEmail);

        Assert.Equal(ShamwariAddOutcome.NowShamwaris, back.Outcome);
        Assert.Equal(alice.Id, Assert.Single(back.List.Shamwaris).User.Id);
        Assert.Equal(bob.Id, Assert.Single((await alice.GetAsync<ShamwariListDto>("api/shamwaris")).Shamwaris).User.Id);
    }

    [Fact]
    public async Task An_invite_becomes_a_request_when_they_sign_up()
    {
        var alice = await app.CreateUserAsync("Alice Inviter");
        await using var aliceHub = await app.ConnectAsync(alice);
        var phone = NewPhone();

        var invited = await AddAsync(alice, phone);
        Assert.Equal(ShamwariAddOutcome.Invited, invited.Outcome);
        var invite = Assert.Single(invited.List.Outgoing);
        Assert.Equal(((Guid?)null, phone), (invite.User?.Id, invite.Contact));

        var aliceNotified = Wait.ForEventAsync(aliceHub, nameof(IChatClient.ShamwarisChanged));
        var bob = await app.CreateUserAsync("Bob Newcomer", phone: phone);
        await aliceNotified;

        var request = Assert.Single((await bob.GetAsync<ShamwariListDto>("api/shamwaris")).Incoming);
        Assert.Equal(alice.Id, request.User?.Id);
        var waiting = Assert.Single((await alice.GetAsync<ShamwariListDto>("api/shamwaris")).Outgoing);
        Assert.Equal((bob.Id, (string?)null), (waiting.User?.Id, waiting.Contact));
    }

    [Fact]
    public async Task Unverified_emails_neither_find_nor_claim()
    {
        var alice = await app.CreateUserAsync("Alice Careful");
        var email = NewEmail("squatter");

        var squatter = await app.CreateUserAsync("Not The Owner", email, verified: false);
        Assert.Equal(ShamwariAddOutcome.Invited, (await AddAsync(alice, email)).Outcome);

        Assert.Empty((await squatter.GetAsync<ShamwariListDto>("api/shamwaris")).Incoming);
        Assert.Single((await alice.GetAsync<ShamwariListDto>("api/shamwaris")).Outgoing, r => r.Contact == email);
    }

    [Fact]
    public async Task Requests_can_be_declined_or_cancelled_and_Shamwaris_removed()
    {
        var alice = await app.CreateUserAsync("Alice Undo");
        var bobEmail = NewEmail("bob");
        var bob = await app.CreateUserAsync("Bob Undo", bobEmail);
        var carolEmail = NewEmail("carol");
        await app.CreateUserAsync("Carol Undo", carolEmail);

        // Bob declines.
        await AddAsync(alice, bobEmail);
        var request = Assert.Single((await bob.GetAsync<ShamwariListDto>("api/shamwaris")).Incoming);
        Assert.Empty((await DeleteAsync(bob, $"api/shamwaris/requests/{request.Id}")).Incoming);
        Assert.Empty((await alice.GetAsync<ShamwariListDto>("api/shamwaris")).Outgoing);

        // Alice cancels her request to Carol, and an invite.
        var toCarol = Assert.Single((await AddAsync(alice, carolEmail)).List.Outgoing);
        Assert.Empty((await DeleteAsync(alice, $"api/shamwaris/requests/{toCarol.Id}")).Outgoing);
        var invite = Assert.Single((await AddAsync(alice, NewEmail("nobody"))).List.Outgoing);
        Assert.Empty((await DeleteAsync(alice, $"api/shamwaris/requests/{invite.Id}")).Outgoing);

        // Asked again, Bob accepts; then Alice unfriends him.
        await AddAsync(alice, bobEmail);
        request = Assert.Single((await bob.GetAsync<ShamwariListDto>("api/shamwaris")).Incoming);
        await bob.PostAsync<ShamwariListDto>($"api/shamwaris/requests/{request.Id}/accept", new { });
        Assert.Empty((await DeleteAsync(alice, $"api/shamwaris/{bob.Id}")).Shamwaris);
        Assert.Empty((await bob.GetAsync<ShamwariListDto>("api/shamwaris")).Shamwaris);
    }

    [Fact]
    public async Task Only_the_person_asked_can_accept()
    {
        var alice = await app.CreateUserAsync("Alice Owner");
        var bobEmail = NewEmail("bob");
        await app.CreateUserAsync("Bob Owner", bobEmail);
        var carol = await app.CreateUserAsync("Carol Meddler");

        var request = Assert.Single((await AddAsync(alice, bobEmail)).List.Outgoing);
        using var response = await carol.Http.PostAsJsonAsync($"api/shamwaris/requests/{request.Id}/accept", new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await alice.GetAsync<ShamwariListDto>("api/shamwaris")).Shamwaris);
    }

    [Theory]
    [InlineData("", "Enter an email address or a phone number.")]
    [InlineData("not-an-email@", "That doesn't look like an email address.")]
    [InlineData("077 123 4567", "Enter the number with its country code, e.g. +263 77 123 4567.")]
    [InlineData("+263 77 abc", "Enter the number with its country code, e.g. +263 77 123 4567.")]
    public async Task Bad_contacts_are_refused_with_a_reason(string contact, string reason)
    {
        var alice = await app.CreateUserAsync("Alice Typo");

        using var response = await alice.Http.PostAsJsonAsync("api/shamwaris", new AddShamwariRequest(contact), ContractJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task You_cannot_add_yourself()
    {
        var email = NewEmail("me");
        var me = await app.CreateUserAsync("Only Me", email);

        using var response = await me.Http.PostAsJsonAsync("api/shamwaris", new AddShamwariRequest(email), ContractJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("That's you.", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("+263 77 123 4567", "+263771234567")]
    [InlineData("00263771234567", "+263771234567")]
    [InlineData("+1 (415) 555-0100", "+14155550100")]
    [InlineData("0771234567", null)]
    [InlineData("+0771234567", null)]
    [InlineData("+263", null)]
    public void Phone_numbers_normalize_to_E164(string typed, string? expected) =>
        Assert.Equal(expected, ShamwariContact.NormalizePhone(typed));
}
