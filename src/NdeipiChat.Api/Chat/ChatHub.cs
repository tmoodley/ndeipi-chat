using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Chat;

/// <summary>
/// Who may follow a realtime topic, for one topic prefix (e.g. "events"). A module registers one
/// for its topics; a topic whose prefix nobody registered can't be subscribed to.
/// </summary>
public interface ITopicPolicy
{
    string Prefix { get; }
    Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct);
}

[Authorize]
public sealed class ChatHub(CurrentUserService users, MessageService messages, ConversationService conversations, IEnumerable<ITopicPolicy> topics) : Hub<IChatClient>
{
    /// <summary>Starts sending this connection <see cref="IChatClient.TopicMessage"/> for the topic, if its policy allows.</summary>
    public async Task Subscribe(string topic)
    {
        var (prefix, key) = Split(topic);
        var policy = topics.FirstOrDefault(p => p.Prefix == prefix);
        var user = await CurrentUserAsync();
        if (policy is null || !await policy.CanSubscribeAsync(user, key, Context.ConnectionAborted))
            throw new HubException("You can't follow that.");
        await Groups.AddToGroupAsync(Context.ConnectionId, ChatNotifier.TopicGroup(topic), Context.ConnectionAborted);
    }

    public Task Unsubscribe(string topic) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, ChatNotifier.TopicGroup(topic), Context.ConnectionAborted);

    static (string Prefix, string Key) Split(string? topic)
    {
        var parts = topic?.Split(':', 2);
        return parts is [var prefix, var key] && prefix.Length is > 0 and <= 32 && key.Length is > 0 and <= 64
            && prefix.All(c => char.IsAsciiLetterLower(c) || c == '-') && key.All(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            ? (prefix, key)
            : throw new HubException("That isn't a topic.");
    }

    public async Task<MessageDto> SendMessage(SendMessageRequest request)
    {
        var user = await CurrentUserAsync();
        try
        {
            return await messages.SendAsync(user, request, Context.ConnectionAborted);
        }
        catch (ChatRejectedException ex)
        {
            // HubException is the only exception whose message reaches the caller.
            throw new HubException(ex.Message);
        }
    }

    public async Task MarkRead(Guid conversationId, Guid messageId)
    {
        var user = await CurrentUserAsync();
        await conversations.MarkReadAsync(user, conversationId, messageId, Context.ConnectionAborted);
    }

    public async Task Typing(Guid conversationId)
    {
        var user = await CurrentUserAsync();
        var others = await conversations.OtherMemberClerkIdsAsync(user, conversationId, Context.ConnectionAborted);
        if (others.Count > 0)
            await Clients.Users(others).Typing(new TypingDto(conversationId, user.Id));
    }

    Task<User> CurrentUserAsync() => users.GetAsync(Context.User!, Context.ConnectionAborted);
}

/// <summary>Delivery to users on every device they're connected from.</summary>
public sealed class ChatNotifier(IHubContext<ChatHub, IChatClient> hub)
{
    public IChatClient ToUsers(IEnumerable<User> users) => ToClerkIds(users.Select(u => u.ClerkUserId));

    public IChatClient ToClerkIds(IEnumerable<string> clerkIds) => hub.Clients.Users(clerkIds.Distinct().ToList());

    public IChatClient ToUser(string clerkId) => hub.Clients.User(clerkId);

    /// <summary>Everyone following a topic (see <see cref="ChatHub.Subscribe"/>).</summary>
    public Task PublishAsync<T>(string topic, T payload) =>
        hub.Clients.Group(TopicGroup(topic)).TopicMessage(new TopicMessageDto(topic, ContractJson.ToElement(payload)));

    public static string TopicGroup(string topic) => "topic:" + topic;
}
