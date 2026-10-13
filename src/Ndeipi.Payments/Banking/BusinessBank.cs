using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Banking;

/// <summary>
/// Ndeipi's business bank, in Ndeipi's vocabulary. <see cref="MeowBank"/> maps Meow's API to it and
/// <see cref="SimulatedBank"/> stands in for sandboxes and tests; nothing outside this folder knows
/// which is in use (SRS §2.2, decoupled gateway).
/// </summary>
public interface IBusinessBank
{
    Task<IReadOnlyList<BankAccountInfo>> ListAccountsAsync(CancellationToken ct);

    Task<BankTransactionPage> ListTransactionsAsync(string accountId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, CancellationToken ct);

    /// <summary>Registers someone to pay. The returned IDs are what payments to them name.</summary>
    Task<BankPayeeRef> CreatePayeeAsync(BankPayeeRequest request, CancellationToken ct);

    /// <summary>
    /// Sends a payment. <see cref="BankPaymentRequest.IdempotencyKey"/> is the same on every retry of
    /// one payment, so the bank sends it once however often this is called.
    /// </summary>
    Task<BankPaymentResult> SendAsync(BankPaymentRequest request, CancellationToken ct);

    Task<BankCard> IssueCardAsync(BankCardRequest request, CancellationToken ct);

    Task<BankCard> UpdateCardAsync(string providerCardId, BankCardUpdate update, CancellationToken ct);

    Task RevokeCardAsync(string providerCardId, CancellationToken ct);

    Task<IReadOnlyList<BankCard>> ListCardsAsync(CancellationToken ct);

    /// <summary>
    /// The card's number, CVC and expiry, from the bank's card vault. The bank hands them out once per
    /// card. The caller passes them straight to the officer and keeps nothing (NFR-SEC-02).
    /// </summary>
    Task<BankCardNumber> RevealCardNumberAsync(string providerCardId, CancellationToken ct);
}

/// <summary>The bank refused the request; the message is safe to show an officer.</summary>
public sealed class BankRefusedException(string message) : Exception(message);

/// <summary>The bank could not be reached after retries, or its circuit is open.</summary>
public sealed class BankUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record BankAccountInfo(
    string Id, string Name, string Type, string? Last4, string Currency, string Status, decimal Available, decimal Current, string? Bank);

public sealed record BankTransaction(
    string Id, DateTimeOffset At, DateTimeOffset? PostedAt, string Description, string? Counterparty,
    decimal Amount, string Currency, bool Credit, string Status, string? Type);

public sealed record BankTransactionPage(IReadOnlyList<BankTransaction> Transactions, string? NextCursor);

public sealed record BankAddress(string Line1, string? Line2, string City, string? State, string PostalCode, string Country);

/// <summary>
/// One payee: a US bank account paid by ACH or by wire, an international beneficiary, or a crypto
/// wallet. The detail record matching <see cref="Kind"/> is set.
/// </summary>
public sealed record BankPayeeRequest(
    PayeeKind Kind,
    string Name,
    string? Email,
    BankAccountDetails? Bank,
    InternationalDetails? International,
    CryptoDetails? Crypto);

/// <summary>A US account. <see cref="BankName"/> and <see cref="BankAddress"/> are needed for wires.</summary>
public sealed record BankAccountDetails(
    string RoutingNumber, string AccountNumber, string AccountType, BankAddress Address, string? BankName, BankAddress? BankAddress);

public sealed record InternationalDetails(
    string BeneficiaryType, string Currency, string BankCountry, string? Iban, string? AccountNumber, string? SwiftBic,
    string? BankName, BankAddress? Address);

/// <summary><see cref="Network"/> is the chain ("base", "ethereum", "solana"…); <see cref="Currency"/> the stablecoin.</summary>
public sealed record CryptoDetails(string Address, string Network, string Currency);

public sealed record BankPayeeRef(string ContactId, string PayeeId);

public sealed record BankPaymentRequest(
    string IdempotencyKey,
    PaymentRail Rail,
    string AccountId,
    string PayeeId,
    decimal Amount,
    string? RecipientCurrency,
    string Purpose,
    string? Memo);

/// <summary>
/// What the bank said. <see cref="Status"/> is the bank's own word ("processing",
/// "pending_approval", "sent"…); <see cref="BankStatuses.ToPayment"/> reads it.
/// </summary>
public sealed record BankPaymentResult(string ApprovalId, string? TransferId, string Status, string? Message);

public sealed record BankCardRequest(
    string IdempotencyKey, string Nickname, string DebitAccountId, int PerTransactionLimit, int? DailyLimit, int? MonthlyLimit,
    IReadOnlyList<string> AllowedCategories, string? Purpose);

/// <summary>Changes to a card; null leaves a field as it is. An empty category list allows all.</summary>
public sealed record BankCardUpdate(
    bool? Frozen, string? Nickname, int? PerTransactionLimit, int? DailyLimit, int? MonthlyLimit, IReadOnlyList<string>? AllowedCategories);

public sealed record BankCard(string Id, string Nickname, string? Last4, string Status, string? Cardholder, string? DebitAccountId);

public sealed record BankCardNumber(string Number, string Cvc, int ExpMonth, int ExpYear);

public static class BankStatuses
{
    /// <summary>The bank's transfer status as a payment status; null if it is not one we know.</summary>
    public static PaymentStatus? ToPayment(string status) => status switch
    {
        "pending_approval" => PaymentStatus.BankApproval,
        "pending" or "processing" => PaymentStatus.Processing,
        "sent" or "completed" => PaymentStatus.Sent,
        "returned" or "refunding" => PaymentStatus.Returned,
        "error" or "failed" or "void" => PaymentStatus.Failed,
        "canceled" => PaymentStatus.Canceled,
        _ => null
    };

    public static CardState ToCard(string status) => status switch
    {
        "active" => CardState.Active,
        "inactive" or "suspended" => CardState.Frozen,
        "closed" => CardState.Closed,
        "failed" => CardState.Failed,
        _ => CardState.Pending
    };

    /// <summary>Statuses after which nothing more happens to a payment.</summary>
    public static bool IsFinal(PaymentStatus status) =>
        status is PaymentStatus.Sent or PaymentStatus.Returned or PaymentStatus.Failed or PaymentStatus.Canceled
            or PaymentStatus.Rejected or PaymentStatus.Expired or PaymentStatus.Blocked;

    /// <summary>
    /// Meow's merchant categories for card controls, exactly as its API spells them.
    /// Allowlist only: the API cannot block a category.
    /// </summary>
    public static readonly IReadOnlyList<string> CardCategories =
    [
        "Advertising", "Airlines", "Books and Newspapers", "Car Rental", "Charity", "Clothing", "Electronics", "Entertainment",
        "Facilities Expenses", "Financial Institutions and Fees", "Fuel", "Furniture", "Gifts and Flowers", "Government Services",
        "Grocery", "Ground Transportation", "Insurance", "Legal", "Lodging", "Meals", "Medical", "Memberships and Dues",
        "Office Supplies", "Parking", "Personal Care", "Political", "Professional Services", "Recruiting", "Rent", "Restaurants",
        "Shipping", "Software", "Taxes", "Taxis and Rideshare", "Technology Infrastructure", "Training and Education",
        "Travel Services", "Utilities", "Vehicle Expenses"
    ];
}
