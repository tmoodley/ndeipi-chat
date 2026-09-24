using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Ndeipi.Api.Auth;
using Ndeipi.Api.Data;
using NdeipiChat.Contracts;

namespace Ndeipi.Api.Chat;

[Authorize]
public sealed class ChatHub(CurrentUserService users, MessageService messages, ConversationService conversations) : Hub<IChatClient>
{
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
}
