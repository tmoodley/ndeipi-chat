using System.ComponentModel.DataAnnotations.Schema;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Data;

/// <summary>A Clerk user, mirrored locally on first sign-in so chats can join and search on it.</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public required string ClerkUserId { get; set; }
    public string DisplayName { get; set; } = "";
    public string? Username { get; set; }
    public string? Email { get; set; }

    /// <summary>Whether Clerk has verified <see cref="Email"/>; only then can Shamwaris find you by it.</summary>
    public bool EmailVerified { get; set; }

    /// <summary>Verified phone number in E.164 form (+263771234567), for finding Shamwaris by number.</summary>
    public string? Phone { get; set; }

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

    /// <summary>Mint a new NFT to the recipient: a post, with <see cref="PostId"/> and <see cref="MetadataUri"/> set.</summary>
    public const string MintOperation = "Mint";

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

    /// <summary>Where a transfer was sent from; null for a mint.</summary>
    public Guid? ConversationId { get; set; }
    public Guid? MessageId { get; set; }
    public string? Memo { get; set; }

    /// <summary>For a mint: the post it mints, and the public URL of its ERC-721 metadata (the tokenURI).</summary>
    public Guid? PostId { get; set; }
    public string? MetadataUri { get; set; }

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

/// <summary>
/// A Shamwari request, or once <see cref="Accepted"/>, the friendship itself. An invite to
/// someone not on Ndeipi yet has no <see cref="AddresseeId"/>, only <see cref="InviteContact"/>,
/// until they sign up with that email or number.
/// </summary>
public sealed class ShamwariLink
{
    public Guid Id { get; set; }
    public Guid RequesterId { get; set; }
    public User Requester { get; set; } = null!;
    public Guid? AddresseeId { get; set; }
    public User? Addressee { get; set; }

    /// <summary>The invited lower-case email or E.164 number, while no user has it yet.</summary>
    public string? InviteContact { get; set; }

    /// <summary>Both user ids in a fixed order, so a pair can only ever have one link.</summary>
    public string? PairKey { get; set; }

    public bool Accepted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
}

/// <summary>
/// A post in the public feed: a caption and one to four photos. When the author mints it,
/// <see cref="MintTransferId"/> points at its row in the Ndeipi queue, which holds the NFT's state.
/// </summary>
public sealed class Post
{
    public Guid Id { get; set; }
    public Guid AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public string? Caption { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? MintTransferId { get; set; }
    public TokenTransfer? Mint { get; set; }

    public List<PostMedia> Media { get; set; } = [];
}

/// <summary>A photo in a post, stored as JPEGs in three sizes (see PostMediaStore).</summary>
public sealed class PostMedia
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public int Position { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}

public sealed class PostLike
{
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
