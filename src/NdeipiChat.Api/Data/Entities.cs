using System.ComponentModel.DataAnnotations.Schema;
using NdeipiChat.Contracts;

namespace Ndeipi.Api.Data;

/// <summary>A Clerk user, mirrored locally on first sign-in so chats can join and search on it.</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public required string ClerkUserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Username { get; set; }
    public string? Email { get; set; }
    public string? AvatarUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ProfileSyncedAt { get; set; }

    public List<UserWallet> Wallets { get; set; } = [];
}

public sealed class UserWallet
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Chain { get; set; }
    public required string Address { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Conversation
{
    public Guid Id { get; set; }
    public ConversationType Type { get; set; }
    public string? Title { get; set; }

    /// <summary>For direct chats, both user ids in a fixed order, so a pair can only ever have one.</summary>
    public string? DirectKey { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastActivityAt { get; set; }

    public List<ConversationMember> Members { get; set; } = [];
}

public sealed class ConversationMember
{
    public Guid ConversationId { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary><see cref="ChatMessage.Seq"/> of the last message this member has read.</summary>
    public long? LastReadSeq { get; set; }
}

public sealed class ChatMessage
{
    public Guid Id { get; set; }

    /// <summary>Insertion order. Timestamps can tie; this can't, so paging never skips a message.</summary>
    public long Seq { get; set; }

    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public required string Kind { get; set; }
    public required string PayloadJson { get; set; }
    public string? StateJson { get; set; }
    public Guid ClientMessageId { get; set; }
    public DateTimeOffset SentAt { get; set; }
}

/// <summary>
/// A row in <c>ndeipi.TokenTransferQueue</c>, the table Ndeipi Enterprise Server reads to put
/// transfers on-chain. This is a contract with another system: see docs/ndeipi-queue.md before
/// renaming anything. Times are UTC <c>datetime2</c> to match Ndeipi's own entities.
/// </summary>
public sealed class TokenTransfer
{
    public const string TransferOperation = "Transfer";

    public Guid Id { get; set; }
    public string Operation { get; set; } = TransferOperation;
    public string Status { get; set; } = TransferStatuses.Pending;

    public required string Chain { get; set; }
    public required string TokenStandard { get; set; }
    public required string TokenSymbol { get; set; }
    public string? ContractAddress { get; set; }
    public string? TokenId { get; set; }
    public int? Decimals { get; set; }
    public decimal Amount { get; set; }

    public Guid SenderUserId { get; set; }
    public required string SenderClerkId { get; set; }
    public string? SenderWalletAddress { get; set; }
    public Guid RecipientUserId { get; set; }
    public required string RecipientClerkId { get; set; }
    public string? RecipientWalletAddress { get; set; }

    public Guid ConversationId { get; set; }
    public Guid MessageId { get; set; }
    public string? Memo { get; set; }

    public string? TxHash { get; set; }
    public string? Error { get; set; }
    public int Attempts { get; set; }
    public string? ClaimedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClaimedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>
    /// Set by a trigger whenever <see cref="Status"/> changes, cleared by the API once the chat has
    /// been told. Ndeipi never needs to touch it.
    /// </summary>
    public bool NotifyPending { get; set; }
}

/// <summary>The user's Bridge customer, KYC progress and custodial wallet.</summary>
public sealed class BankingProfile
{
    public const string None = "none";
    public const string Approved = "approved";

    public Guid UserId { get; set; }
    public string? BridgeCustomerId { get; set; }
    public string? KycLinkId { get; set; }
    public string KycStatus { get; set; } = None;
    public string TosStatus { get; set; } = None;
    public string? KycUrl { get; set; }
    public string? TosUrl { get; set; }
    public string? WalletId { get; set; }
    public string? WalletChain { get; set; }
    public string? WalletAddress { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    [NotMapped]
    public bool CanTransfer => KycStatus == Approved && TosStatus == Approved && WalletId is not null && WalletAddress is not null;
}

/// <summary>A user-to-user payment through Bridge, sent from a chat.</summary>
public sealed class BankTransfer
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid ConversationId { get; set; }
    public Guid SenderId { get; set; }
    public Guid RecipientId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public string? Memo { get; set; }
    public string? BridgeTransferId { get; set; }
    public string Status { get; set; } = TransferStatuses.Pending;
    public string? ProviderState { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One-time code from the mobile sign-in page, redeemable once with the PKCE verifier.</summary>
public sealed class MobileAuthCode
{
    public required string CodeHash { get; set; }
    public Guid UserId { get; set; }
    public required string ClerkSessionId { get; set; }
    public required string RedirectUri { get; set; }
    public required string CodeChallenge { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public required string TokenHash { get; set; }
    public Guid UserId { get; set; }
    public required string ClerkSessionId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>Bridge retries webhooks; this makes a redelivery a no-op.</summary>
public sealed class ProcessedWebhookEvent
{
    public required string EventId { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
