using System.Text.Json;
using System.Text.Json.Serialization;

namespace NdeipiChat.Contracts;

/// <summary>
/// Business Banking (SRS-NDE-MEOW-001): Ndeipi's own treasury at Meow, for treasury officers only.
/// The chat API passes officers' requests to the Ndeipi Enterprise Server, which alone talks to the
/// bank; these shapes are that server's, snake_case with amounts as decimal strings.
/// </summary>
public static class TreasuryContract
{
    public const string AppId = "treasury";
    public const string BasePath = "api/treasury";

    /// <summary>Who may open Business Banking. Admins assign it like any other role.</summary>
    public const string Role = "treasury";

    /// <summary>Live updates: published whenever anything in the treasury changes. Treasury officers only.</summary>
    public const string Topic = "treasury:all";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static string Money(decimal amount, string currency = "USD") =>
        currency == "USD" ? "$" + amount.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture)
            : amount.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + currency;
}

public sealed record TreasuryList<T>(IReadOnlyList<T> Data);

public sealed record TreasurySettingsDto(
    string Provider, decimal ApprovalThreshold, int RequiredApprovals, TimeSpan ApprovalLifetime, bool CardRevealEnabled,
    bool AssistantEnabled, IReadOnlyList<string> CardCategories);

public sealed record TreasuryCursorDto(long Cursor);

public sealed record TreasuryAccountDto(
    string Id, string Name, string Type, string? Last4, string Currency, string Status, decimal Available, decimal Current,
    string? Bank, DateTimeOffset RefreshedAt, bool Stale);

public sealed record TreasuryTransactionDto(
    string Id, DateTimeOffset At, DateTimeOffset? PostedAt, string Description, string? Counterparty, decimal Amount, string Currency,
    string Direction, string Status, string? Type);

public sealed record TreasuryTransactionPageDto(IReadOnlyList<TreasuryTransactionDto> Data, string? NextCursor);

public sealed record TreasuryPayeeDto(
    string Id, string Kind, string Name, string Display, string? Country, string? Currency, string CreatedBy, DateTimeOffset CreatedAt);

public sealed record TreasuryApprovalDto(string Officer, string Decision, string? Note, DateTimeOffset At);

public sealed record TreasuryPaymentDto(
    string Id,
    string Rail,
    string AccountId,
    string PayeeId,
    string PayeeName,
    string PayeeDisplay,
    decimal Amount,
    string? RecipientCurrency,
    string Purpose,
    string? Memo,
    string Status,
    string InitiatorType,
    string InitiatedBy,
    string InitiatedById,
    string? AgentSessionId,
    string? AgentModel,
    int RequiredApprovals,
    IReadOnlyList<TreasuryApprovalDto> Approvals,
    DateTimeOffset? ApprovalExpiresAt,
    string TermsDigest,
    string? ProviderStatus,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DispatchedAt,
    DateTimeOffset? CompletedAt);

public sealed record TreasuryCardDto(
    string Id, string Nickname, string? Last4, string? Cardholder, string DebitAccountId, int PerTransactionLimit, int? DailyLimit,
    int? MonthlyLimit, IReadOnlyList<string> AllowedCategories, string State, string? Purpose, bool NumberRevealed,
    string CreatedBy, DateTimeOffset CreatedAt);

public sealed record TreasuryCardNumberDto(string Number, string Cvc, int ExpMonth, int ExpYear);

public sealed record TreasuryJournalEntryDto(
    long Id, DateTimeOffset At, string ActorType, string Actor, string Action, string? Subject, string? Detail,
    string? AgentSessionId, string? AgentModel);

public sealed record TreasuryAssistantTurnDto(string Role, string Text, DateTimeOffset At, IReadOnlyList<string> DraftedPaymentIds);

public sealed record TreasuryAssistantSessionDto(string Id, DateTimeOffset CreatedAt, IReadOnlyList<TreasuryAssistantTurnDto> Messages);

public sealed record TreasuryAssistantReplyDto(string SessionId, string Text, IReadOnlyList<TreasuryPaymentDto> Drafts);

/// <summary>The server's error body: a stable code, a message for the officer, and per-field details.</summary>
public sealed record TreasuryErrorDto(string Code, string Message, string? Field, IReadOnlyList<TreasuryErrorDetailDto>? Details);

public sealed record TreasuryErrorDetailDto(string Field, string Code, string Message);
