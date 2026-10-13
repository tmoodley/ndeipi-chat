using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Banking;

// The operator API's banking shapes. Snake_case on the wire, amounts as decimal strings (PaymentsJson).

public sealed record BankAccountDto(
    string Id, string Name, string Type, string? Last4, string Currency, string Status, decimal Available, decimal Current,
    string? Bank, DateTimeOffset RefreshedAt, bool Stale);

public sealed record BankTransactionDto(
    string Id, DateTimeOffset At, DateTimeOffset? PostedAt, string Description, string? Counterparty, decimal Amount, string Currency,
    string Direction, string Status, string? Type);

public sealed record BankTransactionPageDto(IReadOnlyList<BankTransactionDto> Data, string? NextCursor);

public sealed record AddressRequest(string? Line1, string? Line2, string? City, string? State, string? PostalCode, string? Country);

/// <summary>
/// A new payee. <c>ach</c> and <c>wire</c> take routing and account numbers (wire also the bank's name
/// and address); <c>international</c> takes the bank country, currency and IBAN or account number with
/// a SWIFT/BIC; <c>crypto</c> takes a wallet address, network and coin.
/// </summary>
public sealed record PayeeCreateRequest(
    PayeeKind? Kind,
    string? Name,
    string? Email,
    string? RoutingNumber,
    string? AccountNumber,
    string? AccountType,
    AddressRequest? Address,
    string? BankName,
    AddressRequest? BankAddress,
    string? BeneficiaryType,
    string? Currency,
    string? BankCountry,
    string? Iban,
    string? SwiftBic,
    string? WalletAddress,
    string? Network,
    string? Coin);

public sealed record PayeeDto(
    string Id, PayeeKind Kind, string Name, string Display, string? Country, string? Currency, string CreatedBy, DateTimeOffset CreatedAt);

public sealed record PaymentCreateRequest(
    PaymentRail? Rail, string? AccountId, string? PayeeId, decimal? Amount, string? RecipientCurrency, string? Purpose, string? Memo);

public sealed record ApprovalDto(string Officer, ApprovalDecision Decision, string? Note, DateTimeOffset At);

/// <summary>
/// A payment. <see cref="TermsDigest"/> fingerprints exactly what is being paid; an approval names it,
/// so what an officer approved is what is sent.
/// </summary>
public sealed record PaymentDto(
    string Id,
    PaymentRail Rail,
    string AccountId,
    string PayeeId,
    string PayeeName,
    string PayeeDisplay,
    decimal Amount,
    string? RecipientCurrency,
    string Purpose,
    string? Memo,
    PaymentStatus Status,
    InitiatorType InitiatorType,
    string InitiatedBy,
    string InitiatedById,
    string? AgentSessionId,
    string? AgentModel,
    int RequiredApprovals,
    IReadOnlyList<ApprovalDto> Approvals,
    DateTimeOffset? ApprovalExpiresAt,
    string TermsDigest,
    string? ProviderStatus,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt);

public sealed record ApproveRequest(string? TermsDigest, string? Note);

public sealed record ReasonRequest(string? Reason);

public sealed record CardCreateRequest(
    string? Nickname, string? DebitAccountId, int? PerTransactionLimit, int? DailyLimit, int? MonthlyLimit,
    IReadOnlyList<string>? AllowedCategories, string? Purpose);

public sealed record CardUpdateRequest(
    bool? Frozen, string? Nickname, int? PerTransactionLimit, int? DailyLimit, int? MonthlyLimit, IReadOnlyList<string>? AllowedCategories);

public sealed record CardDto(
    string Id, string Nickname, string? Last4, string? Cardholder, string DebitAccountId, int PerTransactionLimit, int? DailyLimit,
    int? MonthlyLimit, IReadOnlyList<string> AllowedCategories, CardState State, string? Purpose, bool NumberRevealed,
    string CreatedBy, DateTimeOffset CreatedAt);

/// <summary>The card's details, shown once to the officer who asked. Never stored or logged.</summary>
public sealed record CardNumberDto(string Number, string Cvc, int ExpMonth, int ExpYear);

public sealed record JournalEntryDto(
    long Id, DateTimeOffset At, InitiatorType ActorType, string Actor, string Action, string? Subject, string? Detail,
    string? AgentSessionId, string? AgentModel);

public sealed record BankingSettingsDto(
    string Provider, decimal ApprovalThreshold, int RequiredApprovals, TimeSpan ApprovalLifetime, bool CardRevealEnabled,
    bool AssistantEnabled, IReadOnlyList<string> CardCategories);
