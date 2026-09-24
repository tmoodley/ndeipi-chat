using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Chat;

public sealed class ConversationService(ChatDbContext db, ChatNotifier notifier, TimeProvider clock)
{
    public const int MaxGroupSize = 500;

    public async Task<ConversationDto> CreateAsync(User creator, CreateConversationRequest request, CancellationToken ct)
    {
        var memberIds = (request.MemberIds ?? []).Where(id => id != creator.Id).Distinct().ToList();
        var others = await db.Users.Where(u => memberIds.Contains(u.Id)).ToListAsync(ct);
        if (others.Count != memberIds.Count)
            throw new ChatRejectedException("Some of those people don't exist.");

        var now = clock.GetUtcNow();
        Conversation conversation;
        if (request.Type == ConversationType.Direct)
        {
            if (others.Count != 1)
                throw new ChatRejectedException("A direct chat is between you and one other person.");

            var key = DirectKey(creator.Id, others[0].Id);
            var existing = await db.Conversations.FirstOrDefaultAsync(c => c.DirectKey == key, ct);
            if (existing is not null)
                return (await ListForAsync(creator.Id, existing.Id, ct)).Single();

            conversation = new Conversation { Id = Guid.NewGuid(), Type = ConversationType.Direct, DirectKey = key };
        }
        else
        {
            if (others.Count < 2)
                throw new ChatRejectedException("Add at least two other people to start a group.");
            if (others.Count + 1 > MaxGroupSize)
                throw new ChatRejectedException($"Groups are limited to {MaxGroupSize} people.");

            var title = request.Title?.Trim();
            conversation = new Conversation
            {
                Id = Guid.NewGuid(),
                Type = ConversationType.Group,
                Title = string.IsNullOrEmpty(title) ? null : title[..Math.Min(title.Length, 100)]
            };
        }

        conversation.CreatedAt = now;
        conversation.LastActivityAt = now;
        foreach (var user in others.Prepend(creator))
            conversation.Members.Add(new ConversationMember { UserId = user.Id, JoinedAt = now });
        db.Conversations.Add(conversation);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException) when (conversation.DirectKey is not null)
        {
            // The other person opened the same direct chat at the same moment.
            db.ChangeTracker.Clear();
            var winner = await db.Conversations.FirstAsync(c => c.DirectKey == conversation.DirectKey, ct);
            return (await ListForAsync(creator.Id, winner.Id, ct)).Single();
        }

        // Every member sees the conversation from their own side (title, unread count).
        foreach (var user in others.Prepend(creator))
        {
            var view = (await ListForAsync(user.Id, conversation.Id, ct)).Single();
            await notifier.ToUser(user.ClerkUserId).ConversationUpdated(view);
        }

        return (await ListForAsync(creator.Id, conversation.Id, ct)).Single();
    }

    /// <summary>The user's conversations, most recently active first -- or just one of them.</summary>
    public async Task<IReadOnlyList<ConversationDto>> ListForAsync(Guid userId, Guid? conversationId, CancellationToken ct)
    {
        var mine = db.Members.Where(m => m.UserId == userId && (conversationId == null || m.ConversationId == conversationId));

        var conversations = await db.Conversations.AsNoTracking()
            .Where(c => mine.Any(m => m.ConversationId == c.Id))
            .Include(c => c.Members).ThenInclude(m => m.User)
            .AsSplitQuery()
            .ToListAsync(ct);
        var ids = conversations.Select(c => c.Id).ToList();

        var lastMessages = await db.Messages.AsNoTracking()
            .Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => g.OrderByDescending(m => m.Seq).First())
            .ToListAsync(ct);

        var unread = await db.Messages
            .Where(m => ids.Contains(m.ConversationId) && m.SenderId != userId)
            .Join(mine, m => m.ConversationId, me => me.ConversationId, (m, me) => new { m.ConversationId, m.Seq, me.LastReadSeq })
            .Where(x => x.LastReadSeq == null || x.Seq > x.LastReadSeq)
            .GroupBy(x => x.ConversationId)
            .Select(g => new { ConversationId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.ConversationId, x => x.Count, ct);

        return conversations
            .Select(c => ChatMapper.ToDto(c, userId, lastMessages.FirstOrDefault(m => m.ConversationId == c.Id), unread.GetValueOrDefault(c.Id)))
            .OrderByDescending(c => c.LastActivityAt)
            .ToList();
    }

    /// <summary>A page of history, oldest first, ending just before <paramref name="beforeMessageId"/> (or at the newest).</summary>
    public async Task<IReadOnlyList<MessageDto>> MessagesAsync(Guid userId, Guid conversationId, Guid? beforeMessageId, int take, CancellationToken ct)
    {
        await RequireMemberAsync(userId, conversationId, ct);

        var query = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (beforeMessageId is { } before)
        {
            var seq = await db.Messages.Where(m => m.Id == before && m.ConversationId == conversationId).Select(m => (long?)m.Seq).FirstOrDefaultAsync(ct);
            if (seq is null)
                throw new ChatRejectedException("Unknown message.");
            query = query.Where(m => m.Seq < seq);
        }

        var page = await query.OrderByDescending(m => m.Seq).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct);
        return page.OrderBy(m => m.Seq).Select(ChatMapper.ToDto).ToList();
    }

    public async Task MarkReadAsync(User user, Guid conversationId, Guid messageId, CancellationToken ct)
    {
        var message = await db.Messages.AsNoTracking()
            .Where(m => m.Id == messageId && m.ConversationId == conversationId)
            .Select(m => new { m.Seq })
            .FirstOrDefaultAsync(ct);
        if (message is null)
            return;

        // Only ever forwards: a late receipt from another device can't un-read newer messages.
        var moved = await db.Members
            .Where(m => m.ConversationId == conversationId && m.UserId == user.Id && (m.LastReadSeq == null || m.LastReadSeq < message.Seq))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.LastReadSeq, message.Seq), ct);
        if (moved == 0)
            return;

        var members = await db.Members.Where(m => m.ConversationId == conversationId).Select(m => m.User.ClerkUserId).ToListAsync(ct);
        await notifier.ToClerkIds(members).ReadReceipt(new ReadReceiptDto(conversationId, user.Id, messageId, clock.GetUtcNow()));
    }

    public async Task<IReadOnlyList<string>> OtherMemberClerkIdsAsync(User user, Guid conversationId, CancellationToken ct)
    {
        var members = await db.Members
            .Where(m => m.ConversationId == conversationId)
            .Select(m => new { m.UserId, m.User.ClerkUserId })
            .ToListAsync(ct);
        return members.Any(m => m.UserId == user.Id)
            ? members.Where(m => m.UserId != user.Id).Select(m => m.ClerkUserId).ToList()
            : [];
    }

    async Task RequireMemberAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        if (!await db.Members.AnyAsync(m => m.ConversationId == conversationId && m.UserId == userId, ct))
            throw new ChatRejectedException("You aren't a member of this conversation.");
    }

    static string DirectKey(Guid a, Guid b) => a.CompareTo(b) < 0 ? $"{a:N}:{b:N}" : $"{b:N}:{a:N}";
}

public static class ChatMapper
{
    public static MessageDto ToDto(ChatMessage m) => new(
        m.Id,
        m.ConversationId,
        m.SenderId,
        m.Kind,
        JsonSerializer.Deserialize<JsonElement>(m.PayloadJson),
        m.StateJson is null ? null : JsonSerializer.Deserialize<JsonElement>(m.StateJson),
        m.SentAt,
        m.ClientMessageId);

    public static UserDto ToDto(User u) => new(u.Id, u.DisplayName, u.Username, u.AvatarUrl);

    public static ConversationDto ToDto(Conversation c, Guid viewerId, ChatMessage? lastMessage, int unread)
    {
        var members = c.Members.Select(m => m.User).ToList();
        var others = members.Where(u => u.Id != viewerId).ToList();

        string title;
        string? avatar = null;
        if (c.Type == ConversationType.Direct)
        {
            var other = others.FirstOrDefault();
            title = other?.DisplayName ?? "Chat";
            avatar = other?.AvatarUrl;
        }
        else
        {
            title = c.Title ?? string.Join(", ", others.Take(3).Select(u => u.DisplayName)) + (others.Count > 3 ? "…" : "");
        }

        return new ConversationDto(
            c.Id,
            c.Type,
            title,
            avatar,
            members.Select(ToDto).ToList(),
            lastMessage is null ? null : ToDto(lastMessage),
            unread,
            c.LastActivityAt);
    }
}
