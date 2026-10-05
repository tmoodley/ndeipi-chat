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

    /// <summary>Roles from Clerk's public metadata, comma-separated (e.g. "farmer,trader"); they decide which sub-apps the user gets.</summary>
    public string Roles { get; set; } = "";

    /// <summary>Sub-app ids the user pinned in the launcher, comma-separated, in order; null for the defaults.</summary>
    public string? PinnedApps { get; set; }

    /// <summary>The Social profile: a few lines about them, where they are, and a link.</summary>
    public string? Bio { get; set; }
    public string? City { get; set; }
    public string? Website { get; set; }

    /// <summary>
    /// <see cref="AvatarUrl"/> is a photo uploaded on Social rather than the sign-in account's,
    /// which stays in <see cref="ClerkAvatarUrl"/> for when they remove theirs.
    /// </summary>
    public bool CustomAvatar { get; set; }
    public string? ClerkAvatarUrl { get; set; }

    /// <summary>The banner across the top of their profile.</summary>
    public string? CoverUrl { get; set; }

    /// <summary>The rest of the profile (occupation, interests, jobs, skills, links), as JSON: ProfileDetailsDto.</summary>
    public string? ProfileJson { get; set; }

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

    /// <summary>For money sent in a chat: the message and its conversation.</summary>
    public Guid? MessageId { get; set; }
    public Guid? ConversationId { get; set; }

    /// <summary>For a ticket purchase: the order it pays for.</summary>
    public Guid? OrderId { get; set; }

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

    /// <summary>The group it was posted in, if any. A private group's posts are for its members only.</summary>
    public Guid? GroupId { get; set; }
    public SocialGroup? Group { get; set; }

    public List<PostMedia> Media { get; set; } = [];

    /// <summary>Set on a repost: the post it shares. Its own <see cref="Caption"/> is the reposter's thoughts.</summary>
    public Guid? RepostOfId { get; set; }
    public Post? RepostOf { get; set; }
}

public sealed class PostComment
{
    public Guid Id { get; set; }
    public Guid PostId { get; set; }
    public Guid AuthorId { get; set; }
    public User Author { get; set; } = null!;
    public required string Text { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
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

/// <summary>A stock line in the Inventory sub-app, kept per user.</summary>
public sealed class InventoryItem
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public string? Sku { get; set; }
    public string? Location { get; set; }
    public int Quantity { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An event in the Events sub-app: one organizer, one venue, several ticket tiers.</summary>
public sealed class EventListing
{
    public Guid Id { get; set; }
    public Guid OrganizerId { get; set; }
    public User Organizer { get; set; } = null!;
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public required string Category { get; set; }
    public required string City { get; set; }
    public required string Venue { get; set; }
    public DateTimeOffset StartsAt { get; set; }

    /// <summary>What tickets are priced and paid in: the Ndeipi wallet's currency (Bridge), e.g. "usdc".</summary>
    public required string Currency { get; set; }

    public bool IsPublished { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<TicketTier> Tiers { get; set; } = [];
}

/// <summary>
/// A tier of tickets. <see cref="Held"/> counts seats in live holds, <see cref="Sold"/> seats
/// paid for; what's left to sell is Capacity - Sold - Held, changed only by conditional updates.
/// </summary>
public sealed class TicketTier
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public int Capacity { get; set; }
    public int Sold { get; set; }
    public int Held { get; set; }
    public int Position { get; set; }
}

/// <summary>Seats set aside for one buyer while they pay, for up to ten minutes (SRS FR-2.1).</summary>
public sealed class TicketHold
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid TierId { get; set; }
    public Guid UserId { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class TicketOrder
{
    public Guid Id { get; set; }
    public Guid HoldId { get; set; }
    public Guid EventId { get; set; }
    public Guid TierId { get; set; }
    public Guid BuyerId { get; set; }
    public int Quantity { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// One admission. The holder's device signs its rolling QR codes with a key whose public half is
/// <see cref="HolderPublicKey"/>; gates check them offline against that.
/// </summary>
public sealed class Ticket
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public Guid EventId { get; set; }
    public Guid TierId { get; set; }
    public Guid OwnerId { get; set; }
    public required string Status { get; set; }
    public string? HolderPublicKey { get; set; }
    public DateTimeOffset? AdmittedAt { get; set; }
    public string? AdmittedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Someone an organizer lets scan tickets at an event's gates.</summary>
public sealed class EventValidator
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>
/// Someone offering their work in Gigs, pinned to a place. The exact position is used only to
/// measure distances; others see it rounded (<see cref="NdeipiChat.Contracts.GigsContract.Approximate"/>).
/// </summary>
public sealed class GigProfile
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public string Headline { get; set; } = "";

    /// <summary>Comma-separated <see cref="NdeipiChat.Contracts.GigSkills"/>.</summary>
    public string Skills { get; set; } = "";

    public string? Region { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool IsAvailable { get; set; }
    public int CompletedGigs { get; set; }
    public int RatingSum { get; set; }
    public int RatingCount { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class Gig
{
    public Guid Id { get; set; }
    public Guid ClientId { get; set; }
    public User Client { get; set; } = null!;
    public Guid? WorkerId { get; set; }
    public User? Worker { get; set; }
    public required string Title { get; set; }
    public string Description { get; set; } = "";
    public required string Skill { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? Region { get; set; }
    public decimal Budget { get; set; }
    public required string TokenSymbol { get; set; }
    public required string Status { get; set; }

    /// <summary>The chat the gig was started from, if any.</summary>
    public Guid? SourceConversationId { get; set; }

    /// <summary>The client and worker's direct chat, once assigned: the gig's negotiation thread.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>The chat message carrying the NdeipiCoin payment.</summary>
    public Guid? PaymentMessageId { get; set; }

    public int? WorkerStars { get; set; }
    public int? ClientStars { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? AssignedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>A gig offered to one worker by dispatch (or by the client picking them).</summary>
public sealed class GigOffer
{
    public Guid GigId { get; set; }
    public Guid WorkerId { get; set; }
    public double DistanceKm { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One person following another's posts. One-way, and public.</summary>
public sealed class Follow
{
    public Guid FollowerId { get; set; }
    public Guid FolloweeId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A Social group: people who post together. Public ones anyone can join and read.</summary>
public sealed class SocialGroup
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public string Rules { get; set; } = "";
    public string Icon { get; set; } = "👥";
    public string Tone { get; set; } = "blue";
    public string? Tagline { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? AvatarUrl { get; set; }
    public string? CoverUrl { get; set; }
    public bool IsPrivate { get; set; }
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public List<GroupMember> Members { get; set; } = [];
}

public sealed class GroupMember
{
    public Guid GroupId { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public required string Role { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
}

// ---- Point of Sale ----

/// <summary>A business using POS. Its owner is its first Merchant Admin.</summary>
public sealed class PosMerchant
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public required string Name { get; set; }
    public required string Currency { get; set; }
    public bool TaxInclusive { get; set; }
    public decimal DiscountLimitPercent { get; set; }
    public int LockSeconds { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class PosStore
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public required string Name { get; set; }
    public string? Address { get; set; }
    public decimal TaxRatePercent { get; set; }

    /// <summary>The last receipt number given out here; receipts count up per store.</summary>
    public int ReceiptCount { get; set; }
}

/// <summary>A Ndeipi user working for a merchant, with their role and till PIN.</summary>
public sealed class PosStaff
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public required string Role { get; set; }

    /// <summary>The one store they work at; null for all.</summary>
    public Guid? StoreId { get; set; }

    /// <summary>PBKDF2 of the PIN with <see cref="PinSalt"/>; never the PIN itself.</summary>
    public byte[]? PinHash { get; set; }
    public byte[]? PinSalt { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>A till unlocked by a staff PIN: who's at it, until it locks or expires.</summary>
public sealed class PosTillSession
{
    public Guid Id { get; set; }

    /// <summary>SHA-256 of the token the till holds.</summary>
    public required byte[] TokenHash { get; set; }
    public Guid StoreId { get; set; }
    public Guid StaffId { get; set; }

    /// <summary>The Ndeipi account the till device is signed in with.</summary>
    public Guid DeviceUserId { get; set; }
    public required string Terminal { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class PosCategory
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public required string Name { get; set; }
    public required string Icon { get; set; }
    public required string Tone { get; set; }
    public int Order { get; set; }
}

public sealed class PosProduct
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid? CategoryId { get; set; }
    public required string Name { get; set; }
    public required string Icon { get; set; }
    public string? Sku { get; set; }
    public string? Barcode { get; set; }
    public decimal Price { get; set; }
    public decimal? TaxRatePercent { get; set; }
    public bool IsActive { get; set; }
    public int SafetyStock { get; set; }

    /// <summary>Counted products can't be sold past what's in stock; uncounted ones have no stock.</summary>
    public bool TrackStock { get; set; } = true;

    /// <summary>Variants and modifiers, as JSON (PosVariantDto / PosModifierDto lists).</summary>
    public string? VariantsJson { get; set; }
    public string? ModifiersJson { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>How many of a product (or one variant, by name; "" for the product) a store has. Never below zero.</summary>
public sealed class PosStock
{
    public Guid StoreId { get; set; }
    public Guid ProductId { get; set; }
    public required string Variant { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>
/// A Ndeipi Pay request shown as a QR code on a till: the customer who scans it pays
/// <see cref="Amount"/> from their wallet to the merchant owner's, through <see cref="BankTransferId"/>.
/// </summary>
public sealed class PosQrPayment
{
    public Guid Id { get; set; }

    /// <summary>What the QR code carries: long and random, so it can't be guessed.</summary>
    public required string Code { get; set; }

    public Guid MerchantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid StaffId { get; set; }

    /// <summary>Who's paid: the merchant's owner, whose wallet takes the money.</summary>
    public Guid RecipientId { get; set; }

    public decimal Amount { get; set; }

    /// <summary>The wallet currency it's paid in, e.g. "usdc".</summary>
    public required string Currency { get; set; }

    public required string Status { get; set; }
    public string? Error { get; set; }
    public Guid? PayerId { get; set; }
    public Guid? BankTransferId { get; set; }

    /// <summary>The sale it paid for; a payment pays for one sale only.</summary>
    public Guid? SaleId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PosShift
{
    public Guid Id { get; set; }
    public Guid StoreId { get; set; }
    public Guid StaffId { get; set; }
    public DateTimeOffset OpenedAt { get; set; }
    public decimal OpeningFloat { get; set; }
    public DateTimeOffset? ClosedAt { get; set; }
    public decimal? CountedCash { get; set; }
}

/// <summary>Cash moved in or out of the drawer outside a sale: drops, pay-ins and "no sale" opens.</summary>
public sealed class PosCashMovement
{
    public Guid Id { get; set; }
    public Guid ShiftId { get; set; }
    public Guid StaffId { get; set; }
    public required string Kind { get; set; }
    public decimal Amount { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; }
}

public sealed class PosSale
{
    public Guid Id { get; set; }
    public Guid MerchantId { get; set; }
    public Guid StoreId { get; set; }
    public Guid ShiftId { get; set; }
    public Guid StaffId { get; set; }

    /// <summary>The till's idempotency key: unique per merchant.</summary>
    public Guid ClientSaleId { get; set; }
    public int ReceiptNumber { get; set; }
    public required string Status { get; set; }
    public string? StatusReason { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
    public decimal Change { get; set; }

    /// <summary>When it was rung up; for an offline sale, earlier than it reached the server.</summary>
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public bool Offline { get; set; }
    public DateTimeOffset? ReversedAt { get; set; }

    /// <summary>The shift a refund was paid out of (a void undoes the sale in its own shift).</summary>
    public Guid? ReversedShiftId { get; set; }
    public List<PosSaleLine> Lines { get; set; } = [];
    public List<PosPayment> Payments { get; set; } = [];
}

public sealed class PosSaleLine
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public int Position { get; set; }
    public Guid ProductId { get; set; }
    public required string Name { get; set; }
    public required string Variant { get; set; }
    public string? Modifiers { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Discount { get; set; }
    public decimal TaxRatePercent { get; set; }
    public decimal Tax { get; set; }
    public decimal Total { get; set; }
}

public sealed class PosPayment
{
    public Guid Id { get; set; }
    public Guid SaleId { get; set; }
    public required string Tender { get; set; }
    public decimal Amount { get; set; }
    public string? Reference { get; set; }
}

/// <summary>
/// One entry in a merchant's tamper-evident trail (NFR-SEC-03). <see cref="Hash"/> covers this
/// entry's fields and the previous entry's hash, so editing or deleting any entry breaks the chain.
/// </summary>
public sealed class PosAuditEntry
{
    public long Id { get; set; }
    public Guid MerchantId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset At { get; set; }
    public Guid? StoreId { get; set; }
    public Guid StaffId { get; set; }
    public required string StaffName { get; set; }
    public required string Terminal { get; set; }
    public required string Action { get; set; }
    public required string Details { get; set; }
    public required string PreviousHash { get; set; }
    public required string Hash { get; set; }
}

/// <summary>A fundraising campaign in Donations, run by an organizer.</summary>
public sealed class DonationCampaign
{
    public Guid Id { get; set; }
    public Guid OrganizerId { get; set; }
    public User Organizer { get; set; } = null!;
    public required string Title { get; set; }
    public required string Summary { get; set; }
    public string? Story { get; set; }

    /// <summary>Who the money is for, e.g. "Mbare Children's Home".</summary>
    public required string Beneficiary { get; set; }

    public required string Icon { get; set; }
    public decimal Target { get; set; }

    /// <summary>The wallet currency gifts are paid in, e.g. "usdc".</summary>
    public required string Currency { get; set; }

    /// <summary>Confirmed gifts: kept up to date as each is confirmed, so the feed needn't add them up.</summary>
    public decimal Raised { get; set; }
    public int Gifts { get; set; }

    public bool Verified { get; set; }
    public Guid? VerifiedById { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset? EndsAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A gift to a campaign, paid from the giver's wallet to the organizer's by <see cref="BankTransferId"/>.</summary>
public sealed class Donation
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid DonorId { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public bool Anonymous { get; set; }
    public string? Message { get; set; }
    public required string Status { get; set; }
    public string? Error { get; set; }
    public Guid BankTransferId { get; set; }

    /// <summary>Believe Points it earned, once confirmed.</summary>
    public int Points { get; set; }

    /// <summary>SHA-256 of its receipt (DonationReceipt), fixed when it's confirmed.</summary>
    public string? ReceiptHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>
/// One award of Believe Points (or a deduction), shared across Ndeipi: a person's balance is the
/// sum of theirs. Each is for one thing in one app (<see cref="Source"/> + <see cref="SourceId"/>),
/// so the same thing can't be rewarded twice.
/// </summary>
public sealed class PointsEntry
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public int Points { get; set; }
    public required string Source { get; set; }
    public required string SourceId { get; set; }
    public required string Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An Absa gold mining loan application, from a group, made by one Ndeipi user (Finance).</summary>
public sealed class LoanApplication
{
    public Guid Id { get; set; }
    public required string Reference { get; set; }
    public Guid ApplicantId { get; set; }
    public required string Status { get; set; }

    /// <summary>The stage it's at (or was returned from); null as a draft and once collected or declined.</summary>
    public string? Stage { get; set; }

    // Eligibility (FR-ELG).
    public bool? ZambianOwned { get; set; }
    public string? RegistrationBody { get; set; }
    public string? LicenceType { get; set; }
    public bool? HasBankAccount { get; set; }

    // The group (FR-GRP).
    public string? GroupName { get; set; }
    public string? GroupType { get; set; }

    /// <summary>As on its certificate. One open application per registration number.</summary>
    public string? RegistrationNumber { get; set; }

    public string? Province { get; set; }
    public string? Constituency { get; set; }
    public string? Ward { get; set; }
    public string? Village { get; set; }

    // What it finances (FR-LTY, FR-TRM).
    public string? ClusterType { get; set; }
    public int RunningMonths { get; set; } = 3;

    /// <summary>The estimate when it was last saved, for lists.</summary>
    public decimal EstimateTotal { get; set; }

    /// <summary>Their declaration that the group is Zambian-owned and the information true (FR-SUB-02).</summary>
    public bool Declared { get; set; }

    /// <summary>Absa's reference for the collected loan (FR-DSB).</summary>
    public string? CollectionReference { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }

    public List<LoanApplicationItem> Items { get; set; } = [];
    public List<LoanDocument> Documents { get; set; } = [];
    public List<LoanDecision> Decisions { get; set; } = [];
}

/// <summary>Equipment an application finances, with its prices when the application was last saved.</summary>
public sealed class LoanApplicationItem
{
    public Guid ApplicationId { get; set; }
    public Guid EquipmentId { get; set; }
    public required string Name { get; set; }
    public bool Buy { get; set; }
    public decimal? HirePerMonth { get; set; }
    public decimal? BuyPrice { get; set; }
    public decimal? RunningPerMonth { get; set; }
}

/// <summary>An uploaded document; the file is kept on disk under Finance:DocumentsPath.</summary>
public sealed class LoanDocument
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }
    public required string Kind { get; set; }
    public required string FileName { get; set; }
    public required string ContentType { get; set; }
    public long Size { get; set; }

    /// <summary>Relative to the documents folder.</summary>
    public required string StoredAs { get; set; }

    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>A step in an application's history: submitted, or a committee's decision at a stage.</summary>
public sealed class LoanDecision
{
    public Guid Id { get; set; }
    public Guid ApplicationId { get; set; }

    /// <summary>The stage decided at; "submitted" when the applicant (re)submitted.</summary>
    public required string Stage { get; set; }

    public required string Decision { get; set; }
    public Guid DeciderId { get; set; }
    public Guid? CommitteeId { get; set; }
    public string? Comment { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A committee (or Absa branch) for one stage, covering an area (see LoanCommitteeDto).</summary>
public sealed class LoanCommittee
{
    public Guid Id { get; set; }
    public required string Stage { get; set; }
    public required string Name { get; set; }
    public required string Province { get; set; }
    public string? Constituency { get; set; }
    public string? Ward { get; set; }
    public string? Village { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<LoanCommitteeMember> Members { get; set; } = [];
}

public sealed class LoanCommitteeMember
{
    public Guid CommitteeId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>Equipment a loan can finance, with prices an admin keeps up to date (null: to be confirmed).</summary>
public sealed class LoanEquipment
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string Description { get; set; } = "";
    public required string ClusterType { get; set; }
    public string? Mtp { get; set; }
    public decimal? HirePerMonth { get; set; }
    public decimal? BuyPrice { get; set; }
    public decimal? RunningPerMonth { get; set; }
    public bool Active { get; set; } = true;
    public int Order { get; set; }
}

/// <summary>A constituency in a province, for the location step's list.</summary>
public sealed class FinanceConstituency
{
    public Guid Id { get; set; }
    public required string Province { get; set; }
    public required string Name { get; set; }
}

/// <summary>
/// An outside account someone linked for their Trust Score. Each outside account can back only one
/// Ndeipi account (the anti-sybil rule): <see cref="ExternalId"/> is unique per platform.
/// </summary>
public sealed class TrustLink
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Platform { get; set; }

    /// <summary>The platform's own id for the account.</summary>
    public required string ExternalId { get; set; }

    /// <summary>The name or handle to show its owner, e.g. "@tmoodley".</summary>
    public string? Handle { get; set; }

    /// <summary>X Premium: a paid, verified X account, which counts in tier 2.</summary>
    public bool Premium { get; set; }

    /// <summary>OAuth tokens, encrypted with Trust:TokenKey (TrustTokenProtector); null if there's no key.</summary>
    public string? AccessTokenProtected { get; set; }
    public string? RefreshTokenProtected { get; set; }
    public DateTimeOffset? TokenExpiresAt { get; set; }

    /// <summary>
    /// It comes from an account they sign in to Ndeipi with (Clerk), not from the trust page: it is
    /// confirmed each time their profile syncs, and goes when they remove it from their sign-in.
    /// </summary>
    public bool ViaSignIn { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>When the platform last confirmed the account is still theirs. Credit decays from here.</summary>
    public DateTimeOffset LastConfirmedAt { get; set; }
}

/// <summary>Someone's Trust Score as last worked out, kept so it's quick to show and to broadcast changes.</summary>
public sealed class TrustScoreRecord
{
    public Guid UserId { get; set; }
    public int Score { get; set; }
    public decimal Tier1 { get; set; }
    public decimal Tier2 { get; set; }
    public decimal Tier3 { get; set; }
    public int Settlements { get; set; }
    public decimal HistoryPoints { get; set; }
    public int WorkReferences { get; set; }
    public decimal WorkPoints { get; set; }

    /// <summary>The platforms behind each tier, e.g. "bridge;linkedin;telegram".</summary>
    public string Platforms { get; set; } = "";

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A photo or video uploaded to a chat. Its id is random and unguessable; the files are served by
/// it without sign-in (as Social's photos are), so a chat's media is as private as its link.
/// </summary>
public sealed class ChatMedia
{
    public Guid Id { get; set; }
    public Guid UploaderId { get; set; }

    /// <summary>The chat it was uploaded for; it can only be sent there (or forwarded on by its members).</summary>
    public Guid ConversationId { get; set; }

    /// <summary>The message it was first sent in, once sent.</summary>
    public Guid? MessageId { get; set; }

    public required string Type { get; set; }
    public required string ContentType { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Livestock for sale (Market), first posted in a chat by its seller.</summary>
public sealed class MarketListing
{
    public Guid Id { get; set; }
    public Guid SellerId { get; set; }
    public required string Title { get; set; }
    public required string Species { get; set; }
    public string? Breed { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal? Price { get; set; }
    public required string Currency { get; set; }
    public string? Location { get; set; }
    public string? Age { get; set; }
    public string? Description { get; set; }
    public bool OpenToBarter { get; set; }
    public string? BarterTerms { get; set; }
    public required string Status { get; set; }

    /// <summary>Its photos and videos (ChatMedia ids), in order, comma-separated.</summary>
    public string MediaIds { get; set; } = "";

    /// <summary>Its hashtags, each between semicolons (";goats;boer;") so one can be searched for exactly.</summary>
    public string Tags { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A message showing a listing: where it was first posted, or forwarded to.</summary>
public sealed class MarketListingPost
{
    public Guid MessageId { get; set; }
    public Guid ListingId { get; set; }
    public Guid ConversationId { get; set; }
    public DateTimeOffset PostedAt { get; set; }
}

/// <summary>An offer on a listing, cash or barter, posted as a message in one of its chats.</summary>
public sealed class MarketOffer
{
    public Guid Id { get; set; }
    public Guid ListingId { get; set; }
    public Guid BuyerId { get; set; }
    public Guid MessageId { get; set; }
    public Guid ConversationId { get; set; }
    public required string Kind { get; set; }
    public decimal? Amount { get; set; }
    public string? Currency { get; set; }
    public string? Text { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
}
