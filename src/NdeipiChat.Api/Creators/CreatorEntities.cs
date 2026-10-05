using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Data;

/// <summary>A creator's profile and storefront (Creators, FR-CR-01..03). One per user; the id is the user's.</summary>
public sealed class CreatorProfile
{
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public required string Category { get; set; }
    public string? Bio { get; set; }

    /// <summary>The storefront banner (a CreatorMedia photo).</summary>
    public Guid? BannerMediaId { get; set; }

    /// <summary>Their links as JSON ([{"label":…,"url":…}]).</summary>
    public string LinksJson { get; set; } = "[]";

    public Guid? PinnedPostId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A subscription tier (FR-CR-04): up to five active per creator, ranked 1 (lowest) up.</summary>
public sealed class CreatorTier
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public decimal MonthlyPrice { get; set; }
    public decimal? AnnualPrice { get; set; }
    public int Rank { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A post (FR-CR-05, 08, 10): public, for a tier and up, or pay-per-view; published now or later.</summary>
public sealed class CreatorPost
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public required string Title { get; set; }

    /// <summary>What everyone sees of a locked post; by default the start of the body.</summary>
    public string? Teaser { get; set; }

    /// <summary>The text, as Markdown.</summary>
    public string? Body { get; set; }

    /// <summary>Its photos, audio and video (CreatorMedia ids), in order, comma-separated.</summary>
    public string MediaIds { get; set; } = "";

    public required string Access { get; set; }
    public Guid? TierId { get; set; }
    public decimal? Price { get; set; }
    public DateTimeOffset PublishAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>A creator's uploaded photo, audio or video. Only served through links that expire (NFR-CR-06).</summary>
public sealed class CreatorMedia
{
    public Guid Id { get; set; }
    public Guid CreatorId { get; set; }
    public required string Type { get; set; }
    public required string ContentType { get; set; }
    public string? FileName { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public long Size { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Someone's subscription to a creator's tier (FR-CR-04, 06, 07).</summary>
public sealed class CreatorSubscription
{
    public Guid Id { get; set; }
    public Guid SubscriberId { get; set; }
    public Guid CreatorId { get; set; }
    public Guid TierId { get; set; }
    public required string Period { get; set; }

    /// <summary>Per period, fixed when they subscribed (a later price change doesn't touch it).</summary>
    public decimal Price { get; set; }

    public required string Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset? GraceUntil { get; set; }

    /// <summary>Let the creator see who they are (FR-CR-14); otherwise anonymous.</summary>
    public bool ShareProfile { get; set; }

    /// <summary>When the next renewal payment may be tried (at the period's end, then daily while in grace).</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A payment to a creator: a subscription, a renewal or an unlock. Never deleted or edited apart from
/// its status following its Bridge transfer: the ledger (NFR-CR-05). The fee and its rate are kept as
/// they were at the time.
/// </summary>
public sealed class CreatorPayment
{
    public Guid Id { get; set; }
    public required string Kind { get; set; }
    public Guid PayerId { get; set; }
    public Guid CreatorId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid? PostId { get; set; }
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }
    public decimal FeePercent { get; set; }
    public required string Currency { get; set; }
    public Guid BankTransferId { get; set; }
    public string Status { get; set; } = TransferStatuses.Pending;

    /// <summary>For a subscription or renewal: the period it pays for ends here.</summary>
    public DateTimeOffset? PeriodEnd { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
}

/// <summary>A pay-per-view post someone has bought (FR-CR-05): theirs to see from then on.</summary>
public sealed class CreatorUnlock
{
    public Guid PostId { get; set; }
    public Guid UserId { get; set; }
    public Guid PaymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One person's activity, counted once a day (FR-CR-16): a storefront visit, a post view or a media
/// play. Enough for funnels and post stats without keeping every hit.
/// </summary>
public sealed class CreatorActivity
{
    public const string Visit = "visit", View = "view", Play = "play";

    public long Id { get; set; }
    public Guid CreatorId { get; set; }
    public Guid UserId { get; set; }
    public Guid PostId { get; set; }
    public required string Kind { get; set; }
    public DateOnly Day { get; set; }
}

/// <summary>Where a creator can withdraw to (FR-CR-13): a bank account held by Bridge, or a USDC address.</summary>
public sealed class PayoutAccount
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Rail { get; set; }
    public required string Label { get; set; }

    /// <summary>Bridge's id for the bank account; null for a USDC address.</summary>
    public string? BridgeExternalAccountId { get; set; }

    /// <summary>The USDC address, for <see cref="PayoutRails.Crypto"/>.</summary>
    public string? Address { get; set; }

    /// <summary>What it's paid in: "usd" (ACH, wire), "eur" (SEPA) or the wallet's stablecoin.</summary>
    public required string Currency { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public bool Removed { get; set; }
}

/// <summary>A withdrawal from the creator's Ndeipi wallet to a payout account, through Bridge.</summary>
public sealed class Withdrawal
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PayoutAccountId { get; set; }
    public decimal Amount { get; set; }
    public string Status { get; set; } = PayoutStatuses.Pending;
    public string? BridgeTransferId { get; set; }
    public string? ProviderState { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
