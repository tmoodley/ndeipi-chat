using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Users;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Transfers;

public sealed record TransferSourceRequest(string? Type, string? WalletId, string? Currency, string? Rail);

public sealed record TransferDestinationRequest(string? Type, string? WalletId, string? PayoutAccountId);

public sealed record TransferCreateRequest(
    TransferSourceRequest? Source,
    TransferDestinationRequest? Destination,
    decimal? Amount,
    string? QuoteId,
    string? IntegratorReference,
    Dictionary<string, string>? Metadata,
    bool? DryRun);

/// <summary>The <c>TransferParty</c> object (openapi.yaml): a resolved source or destination.</summary>
public sealed record TransferPartyDto(
    string Type, string? WalletId, string? UserId, string? Asset,
    string? PayoutAccountId = null, string? Currency = null, string? Rail = null);

public sealed record FeeDto(string Type, string Amount, string Unit);

/// <summary>The <c>Receipt</c> object (openapi.yaml), as stored on the transfer.</summary>
public sealed record ReceiptDto(
    string? AmountDebited,
    string? AmountCredited,
    string? EarnedAmount,
    IReadOnlyList<FeeDto> Fees,
    DateTimeOffset InitiatedAt,
    DateTimeOffset? CompletedAt,
    string? AmountExpected = null,
    string? AmountReceived = null,
    string? AmountPaidOut = null,
    string? Rate = null,
    string? RailReference = null);

/// <summary>The <c>Transfer</c> object (openapi.yaml).</summary>
public sealed record TransferDto(
    string Id,
    TransferKind Kind,
    TransferState State,
    ReasonDto? StateReason,
    TransferPartyDto Source,
    TransferPartyDto Destination,
    string Amount,
    string? DepositAccountId,
    JsonElement? DepositInstructions,
    ReceiptDto? Receipt,
    string? IntegratorReference,
    IReadOnlyDictionary<string, string> Metadata,
    int Version,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt)
{
    public string Object => "transfer";
}

/// <summary>The <c>TransferPreview</c> object (openapi.yaml): a dry run that passed every check.</summary>
public sealed record TransferPreviewDto(TransferKind Kind, TransferPartyDto Source, TransferPartyDto Destination, string Amount, PreviewEstimate Estimated)
{
    public string Object => "transfer_preview";
}

public sealed record PreviewEstimate(
    IReadOnlyList<FeeDto> Fees,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AmountCredited,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EarnedAmount,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AmountPaidOut = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Rate = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? SettlementTimeSeconds = null);

/// <summary>Who may move money (FR-USER-05, FR-USER-07).</summary>
public static class TransferRules
{
    public static void RequireCanTransact(PaymentUser user, string who)
    {
        if (user.Status == UserStatus.Deactivated)
            throw PaymentsException.Forbidden("user_deactivated", $"{who} is deactivated and cannot send or receive funds.");
        if (user.KycStatus != KycStatus.Approved || user.TermsStatus != TermsStatus.Approved)
            throw PaymentsException.Forbidden("user_not_approved", $"{who} has not completed verification and accepted the terms.");
    }
}

/// <summary>
/// The pieces every kind of transfer shares: the object as the API returns it, state moves along
/// the documented paths only (FR-XFER-04), and one <c>transfer.state_changed</c> event per move,
/// written in the same transaction as the change.
/// </summary>
public sealed class TransferBook(EventOutbox events, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    PaymentsOptions Options => options.Value;

    /// <summary>Decimal places for an asset or a fiat currency.</summary>
    public int DecimalsOf(string unit) =>
        unit == Options.Coin.Asset ? Options.Coin.Decimals
        : unit == Options.Points.Asset ? Options.Points.Decimals
        : Ramps.PointsPricing.FiatDecimals;

    public Transfer New(TransferKind kind, string sourceType, string destinationType, string asset, decimal amount, TransferCreateRequest request, string? idempotencyKey)
    {
        var now = clock.GetUtcNow();
        return new Transfer
        {
            Id = Ids.New(Ids.Transfer, clock),
            Kind = kind,
            State = TransferStates.Initial(kind),
            SourceType = sourceType,
            DestinationType = destinationType,
            Asset = asset,
            Amount = amount,
            IntegratorReference = request.IntegratorReference,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? [], PaymentsJson.Options),
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    /// <summary>Records <c>transfer.created</c> for a transfer just added to the change tracker.</summary>
    public Task CreatedAsync(Transfer transfer, CancellationToken ct) =>
        events.AddAsync(EventTypes.TransferCreated, "transfer", transfer.Id, transfer.Version, To(transfer), null, ct);

    /// <summary>
    /// Moves a new transfer before its first save, with no event: the creation event already shows
    /// where it starts (a user-to-user transfer or conversion that completes in the same request).
    /// </summary>
    public static void MoveUnsaved(Transfer transfer, TransferState to)
    {
        if (!TransferStates.CanMove(transfer.Kind, transfer.State, to))
            throw new InvalidOperationException($"A {transfer.Kind} transfer cannot move from {transfer.State} to {to}.");
        transfer.State = to;
    }

    /// <summary>Moves a saved transfer and records <c>transfer.state_changed</c>, plus <paramref name="also"/> if given.</summary>
    public async Task MoveAsync(Transfer transfer, TransferState to, ReasonDto? reason, CancellationToken ct, string? also = null)
    {
        var previous = transfer.State;
        MoveUnsaved(transfer, to);
        if (reason is not null)
            transfer.StateReasonJson = JsonSerializer.Serialize(reason, PaymentsJson.Options);
        transfer.Version++;
        transfer.UpdatedAt = clock.GetUtcNow();
        var snapshot = To(transfer);
        await events.AddAsync(EventTypes.TransferStateChanged, "transfer", transfer.Id, transfer.Version, snapshot, new { state = previous }, ct);
        if (also is not null)
            await events.AddAsync(also, "transfer", transfer.Id, transfer.Version, snapshot, null, ct);
    }

    /// <summary>
    /// Loads a transfer for changing inside the caller's transaction, holding its row until commit.
    /// Two callers applying the same outcome (the worker's poll and a webhook, say) then take turns,
    /// and the second sees what the first did instead of posting it again.
    /// </summary>
    public static async Task<Transfer> LockAsync(PaymentsDbContext db, string transferId, TransferState expected, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Lock a transfer inside a transaction.");
        // A copy tracked earlier in this request may be out of date; read the row afresh under the lock.
        foreach (var stale in db.ChangeTracker.Entries<Transfer>().Where(e => e.Entity.Id == transferId).ToList())
            stale.State = EntityState.Detached;
        var transfer = await db.Transfers
            .FromSql($"SELECT * FROM [payments].[Transfers] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {transferId}")
            .SingleAsync(ct);
        if (transfer.State != expected)
            throw PaymentsException.Conflict("invalid_state", $"That transfer is {WireEnum.Name(transfer.State)}, not {WireEnum.Name(expected)}.");
        return transfer;
    }

    public void SetReceipt(Transfer transfer, ReceiptDto receipt) =>
        transfer.ReceiptJson = JsonSerializer.Serialize(receipt, PaymentsJson.Options);

    public ReceiptDto? Receipt(Transfer transfer) =>
        transfer.ReceiptJson is null ? null : JsonSerializer.Deserialize<ReceiptDto>(transfer.ReceiptJson, PaymentsJson.Options);

    public string Format(decimal value, string unit) => Amounts.Format(value, DecimalsOf(unit));

    public TransferDto To(Transfer t)
    {
        var destinationUnit = t.DestinationAsset ?? t.Asset;
        TransferPartyDto source = t.SourceType == "fiat"
            ? new("fiat", null, null, null, Currency: t.Asset, Rail: t.Rail)
            : new(t.SourceType, t.SourceWalletId, t.SourceUserId, t.Asset);
        TransferPartyDto destination = t.DestinationType == "payout_account"
            ? new("payout_account", null, t.DestinationUserId, null, t.PayoutAccountId, destinationUnit, t.Rail)
            : new(t.DestinationType, t.DestinationWalletId, t.DestinationUserId, destinationUnit);

        return new TransferDto(
            t.Id, t.Kind, t.State,
            t.StateReasonJson is null ? null : JsonSerializer.Deserialize<ReasonDto>(t.StateReasonJson, PaymentsJson.Options),
            source, destination,
            Amounts.Format(t.Amount, DecimalsOf(t.Asset)),
            t.DepositAccountId,
            t.DepositInstructionsJson is null ? null : JsonNode.Parse(t.DepositInstructionsJson)!.Deserialize<JsonElement>(),
            Receipt(t),
            t.IntegratorReference,
            JsonSerializer.Deserialize<Dictionary<string, string>>(t.MetadataJson, PaymentsJson.Options) ?? [],
            t.Version, t.CreatedAt, t.UpdatedAt);
    }
}
