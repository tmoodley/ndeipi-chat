namespace Ndeipi.Payments.Data;

/// <summary>Rows that belong to one integrator; the data layer scopes every query to the caller's (SC-05).</summary>
public interface IIntegratorOwned
{
    Guid IntegratorId { get; set; }
}

/// <summary>Rows that are written once and never changed or deleted (SRV-LED-02, SRV-OPS-03).</summary>
public interface IAppendOnly;

/// <summary>An enterprise or institution that builds on the server.</summary>
public sealed class Integrator
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// An integrator's API key. Only a SHA-256 hash is stored; the key is shown once at creation and
/// can be revoked at any time (SRV-OPS-01). Keys carry 256 random bits, so a plain hash is enough.
/// </summary>
public sealed class ApiKey
{
    public Guid Id { get; set; }
    public Guid IntegratorId { get; set; }
    public Api.PaymentsEnvironment Environment { get; set; }
    public required byte[] KeyHash { get; set; }

    /// <summary>The key's last four characters, so people can tell their keys apart.</summary>
    public required string Last4 { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// One idempotency key's first request and response, replayed for 24 hours (FR-CORE-04, SRV-LED-07).
/// <see cref="ResponseStatus"/> is null while the first request is still running.
/// </summary>
public sealed class IdempotencyRecord : IIntegratorOwned
{
    public long Id { get; set; }
    public Guid IntegratorId { get; set; }
    public required string Key { get; set; }

    /// <summary>SHA-256 of method, path and body: a reused key with a different request is refused (FR-CORE-05).</summary>
    public required byte[] RequestHash { get; set; }

    public int? ResponseStatus { get; set; }
    public string? ResponseBody { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// One state-changing API call or operator action (SRV-OPS-03). Records who, what and the outcome;
/// never request or response bodies, which hold personal data (NFR-SEC-03).
/// </summary>
public sealed class AuditEntry : IAppendOnly
{
    public long Id { get; set; }
    public Guid? IntegratorId { get; set; }
    public Guid? ApiKeyId { get; set; }
    public string? Operator { get; set; }
    public required string RequestId { get; set; }
    public required string Method { get; set; }
    public required string Path { get; set; }
    public int StatusCode { get; set; }
    public string? IdempotencyKey { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ------------------------------------------------------------------ Users (M2)

public enum UserType { Individual, Business }
public enum KycStatus { NotStarted, UnderReview, Incomplete, Approved, Rejected }
public enum TermsStatus { Pending, Approved }
public enum UserStatus { Active, Deactivated }

public sealed class PaymentUser : IIntegratorOwned
{
    public required string Id { get; set; }
    public Guid IntegratorId { get; set; }
    public UserType Type { get; set; }
    public required string ExternalReference { get; set; }
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string? BusinessName { get; set; }
    public string? Country { get; set; }
    public UserStatus Status { get; set; }
    public KycStatus KycStatus { get; set; }
    public TermsStatus TermsStatus { get; set; }

    /// <summary>JSON array of { code, message } (FR-USER-06).</summary>
    public string RejectionReasonsJson { get; set; } = "[]";

    public string MetadataJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum OnboardingLinkKind { Kyc, Terms }

/// <summary>
/// A hosted onboarding link issued to a user (FR-USER-03). Issuing new links supersedes the user's
/// earlier ones, so only the latest pair is live.
/// </summary>
public sealed class OnboardingLink : IIntegratorOwned
{
    public long Id { get; set; }
    public Guid IntegratorId { get; set; }
    public required string UserId { get; set; }
    public OnboardingLinkKind Kind { get; set; }
    public required string Url { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ------------------------------------------------------------------ Wallets and transfers (M3)

public enum WalletStatus { Active, Frozen }

/// <summary>
/// A user's balance in one asset (FR-WAL-01): one per user per asset. Its money lives in ledger
/// accounts, one per bucket (available, pending, and earned for points); this row only names them.
/// </summary>
public sealed class Wallet : IIntegratorOwned
{
    public required string Id { get; set; }
    public Guid IntegratorId { get; set; }
    public required string UserId { get; set; }
    public required string Asset { get; set; }
    public int Decimals { get; set; }
    public WalletStatus Status { get; set; }
    public DateTimeOffset? PriceRiskAcknowledgedAt { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A transfer (SRS §4.3 to §4.5): one row for every kind of money movement (DC-04). Its state only
/// moves along <see cref="Transfers.TransferStates"/>.
/// </summary>
public sealed class Transfer : IIntegratorOwned
{
    public required string Id { get; set; }
    public Guid IntegratorId { get; set; }
    public Transfers.TransferKind Kind { get; set; }
    public Transfers.TransferState State { get; set; }

    /// <summary>JSON { code, message } while in an exception state.</summary>
    public string? StateReasonJson { get; set; }

    public required string SourceType { get; set; }
    public string? SourceWalletId { get; set; }
    public string? SourceUserId { get; set; }
    public required string DestinationType { get; set; }
    public string? DestinationWalletId { get; set; }
    public string? DestinationUserId { get; set; }

    public required string Asset { get; set; }
    public decimal Amount { get; set; }

    /// <summary>JSON receipt once funds have moved (FR-XFER-05).</summary>
    public string? ReceiptJson { get; set; }

    public string? IntegratorReference { get; set; }
    public string MetadataJson { get; set; } = "{}";

    /// <summary>The creating request's key: unique per integrator, so a retry finds this transfer (NFR-REL-01).</summary>
    public string? IdempotencyKey { get; set; }

    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ------------------------------------------------------------------ Ledger (M1)

public enum LedgerAccountKind
{
    /// <summary>A user's wallet; one account per bucket.</summary>
    Wallet,

    /// <summary>Funds in flight: deposits not yet credited, payouts not yet confirmed (SRV-LED-05).</summary>
    Suspense,

    /// <summary>
    /// Ndeipi's prefunded float at one provider (a PayPal balance, an Absa account, the Blockfinex
    /// crypto balance), in one asset: the other side of every deposit, payout and conversion there.
    /// Reconciled daily against that provider's own statement (SRV-PROV-06).
    /// </summary>
    ProviderClearing,

    /// <summary>Fees Ndeipi has earned.</summary>
    FeeRevenue,

    /// <summary>
    /// Ndeipi Points in circulation, one account per kind (purchased, earned): the other side of
    /// every point credited to a wallet. Its fiat counterpart is the provider clearing accounts,
    /// which must always cover the purchased points (docs/payments-api/README.md).
    /// </summary>
    PointsIssued,

    /// <summary>Fiat held against purchased points, per currency. Never spent on anything else.</summary>
    PointsReserve,

    /// <summary>Ndeipi's own money, per currency: what it pays the OTC desk with and backs new points from.</summary>
    Treasury,

    /// <summary>Ndeipi's own stock of an opt-in asset (NdeipiCoin): conversions settle from and into it.</summary>
    Inventory
}

public enum LedgerBucket
{
    /// <summary>Spendable now. For points, purchased points: these can be cashed out.</summary>
    Available,

    /// <summary>In flight (SRV-LED-05).</summary>
    Pending,

    /// <summary>Earned points (rewards): spent first, sent like any other, never cashed out.</summary>
    Earned
}

/// <summary>
/// A ledger account in one asset. <see cref="Balance"/> is maintained only by
/// <see cref="Ledger.LedgerService"/>, inside the posting's transaction (SC-02); a database check
/// keeps accounts that may not go negative from doing so (SRV-LED-03).
/// </summary>
public sealed class LedgerAccount : IIntegratorOwned
{
    public required string Id { get; set; }
    public Guid IntegratorId { get; set; }
    public LedgerAccountKind Kind { get; set; }
    public LedgerBucket Bucket { get; set; }

    /// <summary>The wallet this account belongs to, for <see cref="LedgerAccountKind.Wallet"/>.</summary>
    public string? WalletId { get; set; }

    /// <summary>The provider whose float this is, for <see cref="LedgerAccountKind.ProviderClearing"/>: its <c>Name</c>.</summary>
    public string? Provider { get; set; }

    public required string Asset { get; set; }
    public decimal Balance { get; set; }

    /// <summary>System accounts (clearing, suspense) may go negative; wallets never do.</summary>
    public bool AllowNegative { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One balanced, immutable ledger entry: its lines' amounts sum to zero per asset (SRV-LED-01).
/// Corrections are new postings that reverse it (SRV-LED-02).
/// </summary>
public sealed class Posting : IIntegratorOwned, IAppendOnly
{
    public required string Id { get; set; }
    public Guid IntegratorId { get; set; }
    public string? TransferId { get; set; }
    public required string Description { get; set; }

    // Where the posting came from (SRV-LED-06).
    public string? RequestId { get; set; }
    public Guid? ApiKeyId { get; set; }

    /// <summary>
    /// Unique per integrator: even if an idempotency record is lost, the same key cannot post twice
    /// (NFR-REL-01).
    /// </summary>
    public string? IdempotencyKey { get; set; }

    public string? ReversesPostingId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<PostingLine> Lines { get; set; } = [];
}

public sealed class PostingLine : IAppendOnly
{
    public long Id { get; set; }
    public required string PostingId { get; set; }
    public required string AccountId { get; set; }
    public required string Asset { get; set; }

    /// <summary>Positive credits the account, negative debits it.</summary>
    public decimal Amount { get; set; }

    public decimal BalanceAfter { get; set; }
}

// ------------------------------------------------------------------ Treasury

public enum OtcSide { Buy, Sell }

/// <summary>
/// One trade with Blockfinex's OTC desk, recorded by Ndeipi's treasury team after it settles: USD
/// moves between Ndeipi's Absa account and the desk, and NdeipiCoin the other way. The desk works
/// manually, so these rows are how the server learns of a trade, and the latest one sets the price
/// conversions are quoted at. Ndeipi's own money, not any integrator's.
/// </summary>
public sealed class OtcTrade : IAppendOnly
{
    public long Id { get; set; }

    /// <summary>Whether Ndeipi bought NdeipiCoin (paying USD) or sold it (receiving USD).</summary>
    public OtcSide Side { get; set; }

    public decimal CoinAmount { get; set; }
    public decimal UsdAmount { get; set; }

    /// <summary>USD per NdeipiCoin: <see cref="UsdAmount"/> / <see cref="CoinAmount"/>.</summary>
    public decimal UsdPerCoin { get; set; }

    /// <summary>The Absa transfer that paid or received the USD.</summary>
    public required string AbsaReference { get; set; }

    /// <summary>The desk's own trade or settlement reference.</summary>
    public required string DeskReference { get; set; }

    public required string RecordedBy { get; set; }
    public DateTimeOffset ExecutedAt { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
}

// ------------------------------------------------------------------ Events (M1 outbox; delivery is M2)

/// <summary>
/// A webhook event, written in the same transaction as the change it reports (SC-04), so no
/// event is lost and none is sent for a change that rolled back. The dispatcher delivers from here.
/// </summary>
public sealed class EventRecord : IIntegratorOwned, IAppendOnly
{
    public required string Id { get; set; }
    public Guid IntegratorId { get; set; }
    public required string Type { get; set; }
    public required string ObjectType { get; set; }
    public required string ObjectId { get; set; }
    public int ObjectVersion { get; set; }

    /// <summary>The object's snapshot as the API returns it.</summary>
    public required string DataJson { get; set; }

    public string? PreviousAttributesJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public enum WebhookEndpointStatus { Enabled, Disabled }

/// <summary>
/// An integrator's HTTPS endpoint for events (FR-WH-01), with its own Ed25519 key pair. The private
/// key is encrypted with Data Protection and never leaves the server; deleting an endpoint stops
/// deliveries but keeps the row, so past deliveries still name it.
/// </summary>
public sealed class WebhookEndpoint : IIntegratorOwned
{
    public required string Id { get; set; }
    public Guid IntegratorId { get; set; }
    public required string Url { get; set; }
    public WebhookEndpointStatus Status { get; set; }

    /// <summary>JSON array of event types; empty means every type.</summary>
    public string EventTypesJson { get; set; } = "[]";

    public string? Description { get; set; }
    public required string PublicKeyPem { get; set; }
    public required string ProtectedPrivateKey { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}

public enum DeliveryStatus { Pending, Succeeded, Failed, Canceled }

/// <summary>
/// One event on its way to one endpoint (FR-WH-04). Written in the same transaction as the event
/// (SC-04); the dispatcher sends it, retries with backoff for up to two days, then gives up.
/// </summary>
public sealed class EventDelivery : IIntegratorOwned
{
    public long Id { get; set; }
    public Guid IntegratorId { get; set; }
    public required string EventId { get; set; }
    public required string WebhookEndpointId { get; set; }
    public DeliveryStatus Status { get; set; }

    /// <summary>Attempts made so far.</summary>
    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>Claimed by a dispatcher until then, so two instances never send the same delivery at once.</summary>
    public DateTimeOffset? LockedUntil { get; set; }

    public DateTimeOffset? LastAttemptAt { get; set; }
    public int? LastResponseStatus { get; set; }
    public string? LastError { get; set; }

    /// <summary>When retrying stops: two days after the delivery was created.</summary>
    public DateTimeOffset GiveUpAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
