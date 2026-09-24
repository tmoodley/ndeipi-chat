using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class ChatTests(TestApp app) : IClassFixture<TestApp>
{
    static SendMessageRequest Text(Guid conversationId, string text, Guid? clientMessageId = null) =>
        new(conversationId, MessageKinds.Text, ContractJson.ToElement(new TextPayload(text)), clientMessageId ?? Guid.NewGuid());

    [Fact]
    public async Task Messages_arrive_live_and_stay_in_history()
    {
        var alice = await app.CreateUserAsync("Alice Moyo");
        var bob = await app.CreateUserAsync("Bob Dube");
        var chat = await alice.StartDirectChatAsync(bob);
        Assert.Equal("Bob Dube", chat.Title);

        await using var aliceHub = await app.ConnectAsync(alice);
        await using var bobHub = await app.ConnectAsync(bob);
        var received = Wait.ForEventAsync<MessageDto>(bobHub, nameof(IChatClient.MessageReceived));

        var sent = await aliceHub.InvokeAsync<MessageDto>(ChatHubContract.SendMessage, Text(chat.Id, "  Mhoro Bob  "));

        var message = await received;
        Assert.Equal(sent.Id, message.Id);
        Assert.Equal("Mhoro Bob", ContractJson.Read<TextPayload>(message.Payload)!.Text);

        var history = await bob.GetAsync<List<MessageDto>>($"api/conversations/{chat.Id}/messages");
        Assert.Equal(new[] { sent.Id }, history.Select(m => m.Id));

        var bobsView = await bob.GetAsync<ConversationDto>($"api/conversations/{chat.Id}");
        Assert.Equal("Alice Moyo", bobsView.Title);
        Assert.Equal(1, bobsView.UnreadCount);
        Assert.Equal(sent.Id, bobsView.LastMessage?.Id);

        var receipt = Wait.ForEventAsync<ReadReceiptDto>(aliceHub, nameof(IChatClient.ReadReceipt));
        await bobHub.InvokeAsync(ChatHubContract.MarkRead, chat.Id, sent.Id);
        Assert.Equal(bob.Id, (await receipt).UserId);
        Assert.Equal(0, (await bob.GetAsync<ConversationDto>($"api/conversations/{chat.Id}")).UnreadCount);
    }

    [Fact]
    public async Task Resending_a_message_returns_the_original()
    {
        var alice = await app.CreateUserAsync("Alice Resend");
        var bob = await app.CreateUserAsync("Bob Resend");
        var chat = await alice.StartDirectChatAsync(bob);

        var request = Text(chat.Id, "only once");
        var first = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", request);
        var second = await alice.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", request);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await app.DbAsync(db => db.Messages.CountAsync(m => m.ConversationId == chat.Id)));
    }

    [Fact]
    public async Task Outsiders_can_neither_send_nor_read()
    {
        var alice = await app.CreateUserAsync("Alice Private");
        var bob = await app.CreateUserAsync("Bob Private");
        var carol = await app.CreateUserAsync("Carol Outsider");
        var chat = await alice.StartDirectChatAsync(bob);

        await using var carolHub = await app.ConnectAsync(carol);
        var error = await Assert.ThrowsAsync<HubException>(() => carolHub.InvokeAsync<MessageDto>(ChatHubContract.SendMessage, Text(chat.Id, "let me in")));
        Assert.Contains("aren't a member", error.Message);

        using var history = await carol.Http.GetAsync($"api/conversations/{chat.Id}/messages");
        Assert.Equal(HttpStatusCode.BadRequest, history.StatusCode);
        using var conversation = await carol.Http.GetAsync($"api/conversations/{chat.Id}");
        Assert.Equal(HttpStatusCode.NotFound, conversation.StatusCode);
    }

    [Fact]
    public async Task A_pair_of_people_has_one_direct_chat()
    {
        var alice = await app.CreateUserAsync("Alice Pair");
        var bob = await app.CreateUserAsync("Bob Pair");

        var first = await alice.StartDirectChatAsync(bob);
        var again = await alice.StartDirectChatAsync(bob);
        var fromBob = await bob.StartDirectChatAsync(alice);

        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Id, fromBob.Id);
    }

    [Fact]
    public async Task Group_chats_reach_every_member()
    {
        var alice = await app.CreateUserAsync("Alice Group");
        var bob = await app.CreateUserAsync("Bob Group");
        var carol = await app.CreateUserAsync("Carol Group");
        await using var bobHub = await app.ConnectAsync(bob);
        await using var carolHub = await app.ConnectAsync(carol);

        var announced = Wait.ForEventAsync<ConversationDto>(carolHub, nameof(IChatClient.ConversationUpdated));
        var group = await alice.PostAsync<ConversationDto>("api/conversations",
            new CreateConversationRequest(ConversationType.Group, [bob.Id, carol.Id], "Family"));
        Assert.Equal("Family", (await announced).Title);
        Assert.Equal(3, group.Members.Count);

        var toBob = Wait.ForEventAsync<MessageDto>(bobHub, nameof(IChatClient.MessageReceived));
        var toCarol = Wait.ForEventAsync<MessageDto>(carolHub, nameof(IChatClient.MessageReceived));
        var sent = await alice.PostAsync<MessageDto>($"api/conversations/{group.Id}/messages", Text(group.Id, "Sunday lunch?"));

        Assert.Equal(sent.Id, (await toBob).Id);
        Assert.Equal(sent.Id, (await toCarol).Id);
    }

    [Fact]
    public async Task Kinds_without_a_server_handler_are_refused()
    {
        var alice = await app.CreateUserAsync("Alice Kinds");
        var bob = await app.CreateUserAsync("Bob Kinds");
        var chat = await alice.StartDirectChatAsync(bob);

        using var response = await alice.Http.PostAsJsonAsync($"api/conversations/{chat.Id}/messages",
            new SendMessageRequest(chat.Id, "poll", ContractJson.ToElement(new { question = "Tea?" }), Guid.NewGuid()),
            ContractJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("doesn't support 'poll'", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-issuer")]
    [InlineData("wrong-key")]
    [InlineData("foreign-azp")]
    [InlineData("expired")]
    public async Task Only_genuine_current_Clerk_tokens_are_accepted(string problem)
    {
        var user = await app.CreateUserAsync("Token Tester");
        var token = problem switch
        {
            "missing" => null,
            "wrong-issuer" => TestTokens.Create(user.ClerkId, user.SessionId, issuer: "https://someone-else.clerk.accounts.dev"),
            "wrong-key" => TestTokens.Create(user.ClerkId, user.SessionId, key: new RsaSecurityKey(RSA.Create(2048))),
            "foreign-azp" => TestTokens.Create(user.ClerkId, user.SessionId, azp: "https://evil.test"),
            "expired" => TestTokens.Create(user.ClerkId, user.SessionId, lifetime: TimeSpan.FromMinutes(-10)),
            _ => throw new ArgumentOutOfRangeException(nameof(problem))
        };

        using var client = token is null ? app.CreateClient() : app.ClientWithToken(token);
        using var response = await client.GetAsync("api/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Tokens_from_the_trusted_origin_are_accepted()
    {
        var user = await app.CreateUserAsync("Origin Tester");
        using var client = app.ClientWithToken(TestTokens.Create(user.ClerkId, user.SessionId, azp: TestApp.TrustedOrigin));
        using var response = await client.GetAsync("api/me");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
