namespace NdeipiChat.Contracts;

/// <summary>
/// The Creator Module (NDEIPI-SRS-CREATOR-001): creators with a verified identity publish posts,
/// photos, audio and video (FR-CR-08), sell up to five subscription tiers and pay-per-view unlocks
/// (FR-CR-04/05), are paid from subscribers' Ndeipi wallets with the platform's fee split off
/// (FR-CR-06/11), and withdraw to a bank account or a USDC address (FR-CR-13). Locked content is only
/// served through links that expire (FR-CR-09, NFR-CR-06).
/// </summary>
public static class CreatorsContract
{
    public const string AppId = "creators";
    public const string BasePath = "api/creators";

    /// <summary>FR-CR-04: tiers a creator may offer at once.</summary>
    public const int MaxTiers = 5;

    /// <summary>FR-CR-07: how long access lasts after a renewal payment fails, while it's retried.</summary>
    public const int GraceDays = 3;

    /// <summary>FR-CR-13: earnings received within this many days can't be withdrawn yet (anti-fraud hold).</summary>
    public const int HoldDays = 7;

    public const int MaxNameLength = 80;
    public const int MaxBioLength = 1000;
    public const int MaxTitleLength = 150;
    public const int MaxBodyLength = 50_000;
    public const int MaxTeaserLength = 280;
    public const int MaxMediaPerPost = 10;
    public const int MaxBroadcastLength = 2000;

    /// <summary>A photo as uploaded; it's resized and recompressed on the server.</summary>
    public const long MaxImageBytes = 25 * 1024 * 1024;

    /// <summary>Audio and video as uploaded (kept as sent).</summary>
    public const long MaxAudioVideoBytes = 60 * 1024 * 1024;

    /// <summary>Live updates for one person: their subscriptions and, for a creator, their payments. "creator:{userId:N}".</summary>
    public static string Topic(Guid userId) => $"creator:{userId:N}";

    public static readonly IReadOnlyList<FinanceOption> Categories =
    [
        new("education", "Education"),
        new("music", "Music"),
        new("art", "Art & design"),
        new("farming", "Farming & livestock"),
        new("business", "Business & finance"),
        new("tech", "Technology"),
        new("faith", "Faith"),
        new("lifestyle", "Lifestyle"),
        new("news", "News & commentary"),
        new("other", "Other")
    ];

    public static string CategoryLabel(string? code) => Categories.FirstOrDefault(c => c.Code == code)?.Label ?? code ?? "";

    /// <summary>"$4.99" (subscriptions are paid in USD stablecoin from the Ndeipi wallet).</summary>
    public static string Money(decimal amount) =>
        "$" + amount.ToString(amount == Math.Floor(amount) ? "N0" : "N2", System.Globalization.CultureInfo.InvariantCulture);
}

public static class BillingPeriods
{
    public const string Monthly = "monthly";
    public const string Annual = "annual";
}

/// <summary>Who can see a post (FR-CR-05).</summary>
public static class PostAccess
{
    /// <summary>Anyone.</summary>
    public const string Public = "public";

    /// <summary>Subscribers to its tier or a higher one.</summary>
    public const string Tier = "tier";

    /// <summary>Anyone who pays for it once (pay-per-view), and subscribers to its tier if it has one.</summary>
    public const string PayPerView = "ppv";
}

public static class SubscriptionStatuses
{
    /// <summary>Paid up (or the first payment is on its way).</summary>
    public const string Active = "active";

    /// <summary>A renewal payment failed: access continues for <see cref="CreatorsContract.GraceDays"/> days while it's retried.</summary>
    public const string Grace = "grace";

    /// <summary>Ended: cancelled and run out, or not paid after the grace period.</summary>
    public const string Ended = "ended";

    public static bool HasAccess(string status) => status is Active or Grace;
}

public static class CreatorPaymentKinds
{
    public const string Subscription = "subscription";
    public const string Renewal = "renewal";
    public const string Unlock = "unlock";
}

public static class PayoutRails
{
    /// <summary>US bank account by ACH.</summary>
    public const string Ach = "ach";

    /// <summary>US bank account by wire.</summary>
    public const string Wire = "wire";

    /// <summary>A euro bank account by IBAN.</summary>
    public const string Sepa = "sepa";

    /// <summary>USDC to an address on the same chain as Ndeipi wallets (for an exchange or another wallet).</summary>
    public const string Crypto = "crypto";

    public static string Label(string rail) => rail switch
    {
        Ach => "US bank (ACH)",
        Wire => "US bank (wire)",
        Sepa => "Bank (IBAN, SEPA)",
        Crypto => "USDC address",
        _ => rail
    };
}

public static class PayoutStatuses
{
    public const string Pending = "pending";
    public const string Processing = "processing";
    public const string Paid = "paid";
    public const string Failed = "failed";
}

// ---- Creators and storefronts (FR-CR-01..03) ----

public sealed record CreatorLink(string Label, string Url);

/// <summary>Applying to be a creator (FR-CR-01); the identity check must be passed first (FR-CR-02).</summary>
public sealed record SaveCreatorRequest(string Name, string Category, string? Bio, IReadOnlyList<CreatorLink>? Links, Guid? PinnedPostId = null);

/// <param name="Tiers">Active tiers, cheapest first.</param>
/// <param name="MyTier">The viewer's tier with this creator, if subscribed.</param>
public sealed record CreatorDto(
    Guid UserId,
    string Name,
    string Category,
    string? Bio,
    string? AvatarUrl,
    string? BannerUrl,
    IReadOnlyList<CreatorLink> Links,
    Guid? PinnedPostId,
    IReadOnlyList<CreatorTierDto> Tiers,
    int SubscriberCount,
    int PostCount,
    bool IsMe,
    CreatorSubscriptionDto? MySubscription);

public sealed record CreatorCardDto(Guid UserId, string Name, string Category, string? AvatarUrl, string? BannerUrl, decimal? FromPrice, int SubscriberCount);

/// <param name="IsCreator">They have a creator profile.</param>
/// <param name="CanApply">Their identity check is passed (so they may become a creator).</param>
/// <param name="Subscriptions">Their subscriptions to creators, newest first, including ended ones.</param>
public sealed record CreatorsMeDto(bool IsCreator, bool CanApply, decimal FeePercent, IReadOnlyList<CreatorSubscriptionDto> Subscriptions);

// ---- Tiers (FR-CR-04) ----

/// <param name="AnnualPrice">Null when the tier is monthly only.</param>
public sealed record SaveCreatorTierRequest(string Name, string? Description, decimal MonthlyPrice, decimal? AnnualPrice, int Rank, bool Active = true);

/// <param name="Rank">1 is the lowest; a higher tier sees everything a lower one does.</param>
public sealed record CreatorTierDto(Guid Id, string Name, string? Description, decimal MonthlyPrice, decimal? AnnualPrice, int Rank, bool Active, int SubscriberCount);

// ---- Posts (FR-CR-05, 08, 09, 10) ----

/// <summary>Creating or editing a post. <see cref="PublishAt"/> in the future schedules it (FR-CR-10).</summary>
public sealed record SaveCreatorPostRequest(
    string Title,
    string? Body,
    IReadOnlyList<Guid>? MediaIds,
    string Access,
    Guid? TierId,
    decimal? Price,
    DateTimeOffset? PublishAt,
    string? Teaser = null);

/// <summary>A photo, audio or video in a post. <see cref="Url"/> works for a while only (it expires).</summary>
public sealed record CreatorMediaDto(Guid Id, string Type, string? Url, string? ThumbUrl, int? Width, int? Height, long Size, string? FileName);

/// <param name="Locked">The viewer can't see it: <see cref="Body"/> and media URLs are left out, the teaser shows.</param>
/// <param name="TierName">The tier it's for, if any.</param>
/// <param name="Views">For its creator only.</param>
public sealed record CreatorPostDto(
    Guid Id,
    Guid CreatorId,
    string CreatorName,
    string Title,
    string? Teaser,
    string? Body,
    IReadOnlyList<CreatorMediaDto> Media,
    string Access,
    Guid? TierId,
    string? TierName,
    decimal? Price,
    bool Locked,
    bool Unlocked,
    bool IsScheduled,
    DateTimeOffset PublishAt,
    int? Views);

public sealed record CreatorPostPageDto(IReadOnlyList<CreatorPostDto> Posts, DateTimeOffset? Before);

// ---- Subscribing and paying (FR-CR-06, 07) ----

/// <param name="ShareProfile">Show the creator who they are (otherwise they're anonymous in the creator's list, FR-CR-14).</param>
public sealed record CreatorSubscribeRequest(Guid TierId, string Period, bool ShareProfile = false);

/// <param name="Price">Per period, as it was when they subscribed.</param>
/// <param name="CancelAtPeriodEnd">It won't renew; access lasts until <see cref="CurrentPeriodEnd"/>.</param>
/// <param name="GraceUntil">While in grace: when access stops unless a payment goes through.</param>
/// <param name="LastPaymentStatus">The latest payment's transfer status ("processing", "confirmed", "failed").</param>
public sealed record CreatorSubscriptionDto(
    Guid Id,
    Guid CreatorId,
    string CreatorName,
    Guid TierId,
    string TierName,
    string Period,
    decimal Price,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset CurrentPeriodEnd,
    bool CancelAtPeriodEnd,
    DateTimeOffset? GraceUntil,
    string? LastPaymentStatus,
    string? LastPaymentError);

// ---- The creator's studio (FR-CR-12, 14, 15, 16) ----

/// <param name="Mrr">Monthly recurring revenue, net of the fee: active subscriptions, annual ones as a twelfth.</param>
/// <param name="Withdrawable">What can be withdrawn now: the wallet balance less earnings still on hold.</param>
public sealed record CreatorEarningsDto(
    decimal GrossThisMonth,
    decimal FeesThisMonth,
    decimal NetThisMonth,
    decimal GrossAllTime,
    decimal NetAllTime,
    decimal Mrr,
    int ActiveSubscribers,
    decimal Balance,
    decimal OnHold,
    decimal Withdrawable,
    decimal FeePercent,
    IReadOnlyList<CreatorPaymentDto> Recent);

public sealed record CreatorPaymentDto(Guid Id, string Kind, string? Payer, decimal Amount, decimal Fee, decimal Net, string Status, DateTimeOffset CreatedAt, string? What);

/// <param name="Name">Their name if they chose to share it, otherwise an anonymous label (FR-CR-14).</param>
public sealed record SubscriberDto(string Name, bool Anonymous, string TierName, string Period, string Status, DateTimeOffset Since, int Months);

public sealed record CreatorBroadcastRequest(string Text, Guid? TierId = null);

public sealed record BroadcastResultDto(int Sent);

/// <summary>FR-CR-16, the last 30 days: storefront visits to new subscribers, and how posts did.</summary>
public sealed record CreatorAnalyticsDto(
    int StorefrontVisitors,
    int NewSubscribers,
    double ConversionPercent,
    int PostViews,
    int MediaPlays,
    int Unlocks,
    IReadOnlyList<CreatorPostStatsDto> TopPosts);

public sealed record CreatorPostStatsDto(Guid PostId, string Title, int Views, int Plays, int Unlocks);

// ---- Payouts (FR-CR-13) ----

/// <summary>
/// A bank account or address to withdraw to. Bank details go straight to Bridge, which holds them;
/// Ndeipi keeps only Bridge's id and the last four digits.
/// </summary>
public sealed record SavePayoutAccountRequest(
    string Rail,
    string OwnerName,
    string? BankName = null,
    string? AccountNumber = null,
    string? RoutingNumber = null,
    string? CheckingOrSavings = null,
    string? Iban = null,
    string? Bic = null,
    string? Country = null,
    string? StreetLine1 = null,
    string? City = null,
    string? State = null,
    string? PostalCode = null,
    string? Address = null);

public sealed record PayoutAccountDto(Guid Id, string Rail, string Label, DateTimeOffset CreatedAt);

public sealed record WithdrawRequest(Guid PayoutAccountId, decimal Amount);

public sealed record WithdrawalDto(Guid Id, string AccountLabel, decimal Amount, string Status, string? Error, DateTimeOffset CreatedAt);

public sealed record PayoutsDto(decimal Withdrawable, decimal OnHold, IReadOnlyList<PayoutAccountDto> Accounts, IReadOnlyList<WithdrawalDto> Withdrawals);

/// <summary>What a creator's topic carries: something changed, so reload.</summary>
public sealed record CreatorNewsDto(string What, Guid? Id = null);
