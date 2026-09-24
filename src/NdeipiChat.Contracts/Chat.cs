using System.Text.Json;

namespace NdeipiChat.Contracts;

public sealed record UserDto(Guid Id, string DisplayName, string? Username, string? AvatarUrl);

public sealed record MeDto(
    Guid Id,
    string DisplayName,
    string? Username,
    string? Email,
    string? AvatarUrl,
    IReadOnlyList<UserWalletDto> Wallets,
    BankingStatusDto Banking);

/// <summary>A wallet address the user receives tokens at, one per chain.</summary>
public sealed record UserWalletDto(string Chain, string Address);

public enum ConversationType
{
    Direct,
    Group
}

/// <summary>
/// A conversation as one member sees it: a direct chat is titled with the other person's name,
/// and <see cref="UnreadCount"/> is that member's own.
/// </summary>
public sealed record ConversationDto(
    Guid Id,
    ConversationType Type,
    string Title,
    string? AvatarUrl,
    IReadOnlyList<UserDto> Members,
    MessageDto? LastMessage,
    int UnreadCount,
    DateTimeOffset LastActivityAt);

public sealed record CreateConversationRequest(ConversationType Type, IReadOnlyList<Guid> MemberIds, string? Title);

/// <summary>
/// One chat message. <see cref="Kind"/> selects the extension that validates it on the server and
/// renders it in the app; <see cref="Payload"/> is what the sender sent (never changes) and
/// <see cref="State"/> is what has happened since (e.g. a transfer's status), updated through
/// <see cref="IChatClient.MessageStateChanged"/>.
/// </summary>
public sealed record MessageDto(
    Guid Id,
    Guid ConversationId,
    Guid SenderId,
    string Kind,
    JsonElement Payload,
    JsonElement? State,
    DateTimeOffset SentAt,
    Guid ClientMessageId);

public sealed record MessageStateDto(Guid ConversationId, Guid MessageId, JsonElement State);

/// <summary>
/// <see cref="ClientMessageId"/> is generated once by the app per message. Sending the same id
/// again returns the original message instead of creating a second one -- which matters when the
/// message moves money and the first attempt timed out.
/// </summary>
public sealed record SendMessageRequest(Guid ConversationId, string Kind, JsonElement Payload, Guid ClientMessageId);

public sealed record TypingDto(Guid ConversationId, Guid UserId);

public sealed record ReadReceiptDto(Guid ConversationId, Guid UserId, Guid MessageId, DateTimeOffset ReadAt);

public static class MessageKinds
{
    public const string Text = "text";
    public const string AssetTransfer = "asset.transfer";
    public const string BankTransfer = "bank.transfer";
}
