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
