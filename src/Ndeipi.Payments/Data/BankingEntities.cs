namespace Ndeipi.Payments.Data;

// ------------------------------------------------------------------ Business banking (Meow)
//
// Ndeipi's own treasury at its business bank (SRS-NDE-MEOW-001). None of it belongs to an
// integrator: officers reach it through the operator API, and the bank sees one business, Ndeipi.

/// <summary>
/// A bank account as the bank last reported it, kept so the treasury screen can show balances at
/// once while fresh ones load (NFR-PERF-03). The bank is the source of truth; this is a cache.
/// </summary>
public sealed class TreasuryAccount
{
    /// <summary>The bank's account ID.</summary>
    public required string Id { get; set; }

    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Last4 { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public decimal Available { get; set; }
    public decimal Current { get; set; }
    public string? Bank { get; set; }
    public DateTimeOffset RefreshedAt { get; set; }
}

public enum PayeeKind { Ach, Wire, International, Crypto }

/// <summary>
/// Someone the treasury pays (FR-1.3), registered at the bank as a contact. Names and bank details
/// are encrypted at rest; the screen shows only <see cref="Display"/>, a masked summary.
/// </summary>
public sealed class Payee
{
    public required string Id { get; set; }
    public PayeeKind Kind { get; set; }
    public required string Name { get; set; }

    /// <summary>"Chase ••1234", "Base 0x12…89ab": enough to recognise, never the full number.</summary>
    public required string Display { get; set; }

    /// <summary>ISO 3166-1 alpha-2 of the payee's bank, where known.</summary>
    public string? Country { get; set; }

    /// <summary>For international payees, the currency they are paid in.</summary>
    public string? Currency { get; set; }

    /// <summary>For crypto payees, the address, so it can be screened again before each payment (NFR-CMP-01).</summary>
    public string? Address { get; set; }

    public required string ProviderContactId { get; set; }

    /// <summary>What the bank wants in a payment to this payee: a counterparty, beneficiary or crypto ID.</summary>
    public required string ProviderPayeeId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
}

public enum PaymentRail { Ach, Wire, International, Crypto }

public enum InitiatorType { Officer, Agent }

/// <summary>
/// Where a treasury payment stands. Ndeipi's own steps come first (confirmation of an assistant's
/// draft, screening, dual approval); once dispatched, the bank's status takes over.
/// </summary>
public enum PaymentStatus
{
    /// <summary>Drafted by the assistant; nothing happens until the officer confirms it (FR-5.2).</summary>
    AwaitingConfirmation,

    /// <summary>At or over the approval threshold: waiting for other officers (FR-3.3).</summary>
    PendingApproval,

    /// <summary>Cleared by Ndeipi and being sent to the bank; retried with the same key until it lands.</summary>
    Approved,

    /// <summary>The bank holds it for approval in its own dashboard under its security policy.</summary>
    BankApproval,

    Processing,
    Sent,
    Returned,
    Failed,
    Canceled,
    Rejected,
    Expired,

    /// <summary>The payee matched the sanctions list; never sent (NFR-CMP-01).</summary>
    Blocked
}

/// <summary>
/// One outgoing payment from the treasury (SRS PaymentInstruction). Its ID is the idempotency key
/// at the bank, so however often the dispatch is retried, the bank moves the money once (FR-3.2).
/// </summary>
public sealed class TreasuryPayment
{
    public required string Id { get; set; }
    public PaymentRail Rail { get; set; }
    public required string AccountId { get; set; }
    public required string PayeeId { get; set; }

    /// <summary>US dollars debited (for international payments, the dollars converted).</summary>
    public decimal Amount { get; set; }

    /// <summary>For international payments, the currency the payee receives.</summary>
    public string? RecipientCurrency { get; set; }

    public required string Purpose { get; set; }
    public string? Memo { get; set; }
    public PaymentStatus Status { get; set; }

    /// <summary>The caller's Idempotency-Key: a second request with it gets this payment back.</summary>
    public required string RequestKey { get; set; }

    public InitiatorType InitiatorType { get; set; }

    /// <summary>The officer who asked for it, or who confirmed the assistant's draft.</summary>
    public required string InitiatedBy { get; set; }

    /// <summary>The officer's stable identity, compared for four eyes.</summary>
    public required string InitiatedById { get; set; }

    /// <summary>For assistant drafts (FR-5.3): the conversation and the model that proposed it.</summary>
    public string? AgentSessionId { get; set; }
    public string? AgentModel { get; set; }

    /// <summary>Approvals needed from officers other than the initiator; 0 below the threshold.</summary>
    public int RequiredApprovals { get; set; }

    public DateTimeOffset? ApprovalExpiresAt { get; set; }

    public string? ProviderApprovalId { get; set; }
    public string? ProviderTransferId { get; set; }
    public string? ProviderStatus { get; set; }
    public string? FailureReason { get; set; }

    /// <summary>Dispatch attempts so far, and when the next is due while the bank is unreachable.</summary>
    public int DispatchAttempts { get; set; }
    public DateTimeOffset? NextDispatchAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Version { get; set; } = 1;
}

public enum ApprovalDecision { Approve, Reject }

/// <summary>
/// One officer's decision on a payment (SRS ApprovalRequest). It names the exact terms approved by
/// their digest, so an approval cannot be carried over to a payment that changed.
/// </summary>
public sealed class PaymentApproval : IAppendOnly
{
    public long Id { get; set; }
    public required string PaymentId { get; set; }
    public required string OfficerId { get; set; }
    public required string Officer { get; set; }
    public ApprovalDecision Decision { get; set; }
    public required string TermsDigest { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset At { get; set; }
}

public enum CardState { Pending, Active, Frozen, Closed, Failed }

/// <summary>
/// A virtual corporate card (SRS CorporateCardBinding). Only the last four digits are kept; the
/// number itself never touches this database (NFR-SEC-02).
/// </summary>
public sealed class CorporateCard
{
    public required string Id { get; set; }
    public required string ProviderCardId { get; set; }
    public required string Nickname { get; set; }
    public string? Last4 { get; set; }
    public string? Cardholder { get; set; }
    public required string DebitAccountId { get; set; }
    public int PerTransactionLimit { get; set; }
    public int? DailyLimit { get; set; }
    public int? MonthlyLimit { get; set; }

    /// <summary>Merchant categories the card works at, comma-separated; empty for all.</summary>
    public string AllowedCategories { get; set; } = "";

    public CardState State { get; set; }
    public string? Purpose { get; set; }

    /// <summary>The bank hands out a card's number once; after that it can only be revoked and replaced.</summary>
    public bool NumberRevealed { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Version { get; set; } = 1;
}

public enum BankEventStatus { Pending, Processed, Ignored, Failed }

/// <summary>
/// A signed webhook from the bank, kept as received (SRS WebhookEventLog) for idempotency and
/// replay: the same event ID is stored once, however often the bank delivers it.
/// </summary>
public sealed class BankWebhookEvent
{
    public long Id { get; set; }
    public required string EventId { get; set; }
    public required string EventType { get; set; }
    public required string RawPayload { get; set; }
    public required string Signature { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public BankEventStatus Status { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// The treasury's write-once journal (NFR-SEC-03): every payment step, approval, card change and
/// bank event, with who did it. Rows are never changed or deleted; the database refuses it too.
/// </summary>
public sealed class BankingJournalEntry : IAppendOnly
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public InitiatorType ActorType { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public string? Subject { get; set; }
    public string? Detail { get; set; }

    /// <summary>For assistant actions (FR-5.3): the conversation and the model.</summary>
    public string? AgentSessionId { get; set; }
    public string? AgentModel { get; set; }
}

/// <summary>A conversation with the banking assistant, for one officer.</summary>
public sealed class AssistantSession
{
    public required string Id { get; set; }
    public required string OfficerId { get; set; }
    public required string Officer { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One message in an assistant conversation, kept exactly as sent or received so the next request
/// replays the history unchanged: Claude checks that its earlier reasoning was not edited.
/// </summary>
public sealed class AssistantMessage : IAppendOnly
{
    public long Id { get; set; }
    public required string SessionId { get; set; }

    /// <summary>"user" or "assistant".</summary>
    public required string Role { get; set; }

    /// <summary>The content blocks as a JSON array, in the API's wire format.</summary>
    public required string Content { get; set; }

    public DateTimeOffset At { get; set; }
}
