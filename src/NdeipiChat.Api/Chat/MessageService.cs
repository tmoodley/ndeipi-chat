using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Chat;

/// <summary>The one path every message takes, whether it arrives over SignalR or HTTP.</summary>
public sealed class MessageService(
    ChatDbContext db,
    IEnumerable<IMessageKindHandler> handlers,
    ChatNotifier notifier,
    TimeProvider clock,
    ILogger<MessageService> log)
{
    readonly Dictionary<string, IMessageKindHandler> _handlers = handlers.ToDictionary(h => h.Kind, StringComparer.Ordinal);

    public async Task<MessageDto> SendAsync(User sender, SendMessageRequest request, CancellationToken ct)
    {
        if (request.ClientMessageId == Guid.Empty)
            throw new ChatRejectedException("clientMessageId is required.");
        if (!_handlers.TryGetValue(request.Kind ?? "", out var handler))
            throw new ChatRejectedException($"This server doesn't support '{request.Kind}' messages.");

        // A retry of a message that already went through gets the original back -- never a second
        // transfer.
        if (await FindSentAsync(sender.Id, request.ClientMessageId, ct) is { } existing)
            return existing;

        var conversation = await db.Conversations
            .Include(c => c.Members).ThenInclude(m => m.User)
            .FirstOrDefaultAsync(c => c.Id == request.ConversationId, ct);
        if (conversation is null || conversation.Members.All(m => m.UserId != sender.Id))
            throw new ChatRejectedException("You aren't a member of this conversation.");

        var context = new MessageContext(sender, conversation, conversation.Members.Select(m => m.User).ToList(), Guid.NewGuid());
        var prepared = await handler.PrepareAsync(context, request.Payload, ct);

        var now = clock.GetUtcNow();
        var message = new ChatMessage
        {
            Id = context.MessageId,
            ConversationId = conversation.Id,
            SenderId = sender.Id,
            Kind = handler.Kind,
            PayloadJson = prepared.Payload.GetRawText(),
            StateJson = prepared.InitialState is null ? null : ContractJson.Write(prepared.InitialState),
            ClientMessageId = request.ClientMessageId,
            SentAt = now
        };
        db.Messages.Add(message);
        conversation.LastActivityAt = now;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two copies of the same send raced; the unique (sender, clientMessageId) index let
            // exactly one in. Hand back that one.
            db.ChangeTracker.Clear();
            if (await FindSentAsync(sender.Id, request.ClientMessageId, ct) is { } raced)
                return raced;
            throw;
        }

        var dto = ChatMapper.ToDto(message);
        await notifier.ToUsers(context.Members).MessageReceived(dto);

        try
        {
            // Not tied to the sender's connection: once the message exists, its side effects must finish.
            await handler.AfterSendAsync(context, message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "After-send step failed for {Kind} message {MessageId}", handler.Kind, message.Id);
        }

        return dto;
    }

    async Task<MessageDto?> FindSentAsync(Guid senderId, Guid clientMessageId, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(m => m.SenderId == senderId && m.ClientMessageId == clientMessageId, ct);
        return message is null ? null : ChatMapper.ToDto(message);
    }
}

/// <summary>Updates a message's state and tells everyone in the conversation.</summary>
public sealed class MessageStateService(ChatDbContext db, ChatNotifier notifier)
{
    public async Task SetAsync(Guid messageId, object state, CancellationToken ct)
    {
        var message = await db.Messages.FirstOrDefaultAsync(m => m.Id == messageId, ct);
        if (message is null)
            return;

        message.StateJson = ContractJson.Write(state);
        await db.SaveChangesAsync(ct);

        var members = await db.Members
            .Where(m => m.ConversationId == message.ConversationId)
            .Select(m => m.User.ClerkUserId)
            .ToListAsync(ct);
        await notifier.ToClerkIds(members)
            .MessageStateChanged(new MessageStateDto(message.ConversationId, message.Id, ContractJson.ToElement(state)));
    }
}
