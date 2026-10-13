using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
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

public sealed record TransferPartyDto(string Type, string? WalletId, string? UserId, string? Asset);

public sealed record FeeDto(string Type, string Amount, string Unit);

/// <summary>The <c>Receipt</c> object (openapi.yaml), as stored on the transfer.</summary>
public sealed record ReceiptDto(
    string? AmountDebited,
    string? AmountCredited,
    string? EarnedAmount,
    IReadOnlyList<FeeDto> Fees,
    DateTimeOffset InitiatedAt,
    DateTimeOffset? CompletedAt);

/// <summary>The <c>Transfer</c> object (openapi.yaml).</summary>
public sealed record TransferDto(
    string Id,
    TransferKind Kind,
    TransferState State,
    ReasonDto? StateReason,
    TransferPartyDto Source,
    TransferPartyDto Destination,
    string Amount,
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

public sealed record PreviewEstimate(IReadOnlyList<FeeDto> Fees, string AmountCredited, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? EarnedAmount);

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
/// Transfers (SRS §4.3). In M3, user to user: one user's wallet to another's in the same asset,
/// posted on the ledger in one transaction with the transfer and its event, without any provider
/// (SRV-LED-04). Points keep their kind: the sender's earned points go first and arrive as earned
/// points, so a reward never becomes cashable by changing hands. Conversions and ramps are M4.
/// </summary>
public sealed class TransferService(
    PaymentsDbContext db, LedgerService ledger, EventOutbox events, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    const int Attempts = 3;

    public async Task<object> CreateAsync(TransferCreateRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var (source, destination, amount) = ValidateShape(request);

        if (source.Type == "fiat" || destination.Type == "payout_account")
            throw PaymentsException.NotImplemented("M4");

        var from = await WalletAsync(source.WalletId!, "source.wallet_id", ct);
        var to = await WalletAsync(destination.WalletId!, "destination.wallet_id", ct);
        if (from.Id == to.Id)
            throw PaymentsException.Unprocessable("same_source_and_destination", "The source and destination are the same wallet.", "destination.wallet_id");
        if (from.Asset != to.Asset)
        {
            if (from.UserId == to.UserId)
                throw PaymentsException.NotImplemented("M4"); // a conversion
            throw PaymentsException.Unprocessable("asset_mismatch", $"The wallets hold {from.Asset} and {to.Asset}.", "destination.wallet_id");
        }
        if (request.QuoteId is not null)
            throw PaymentsException.Validation([new("quote_id", "not_allowed", "quote_id is only for conversions.")]);
        Amounts.RequireValid(amount, from.Decimals, from.Asset);

        var users = await db.Users.AsNoTracking().Where(u => u.Id == from.UserId || u.Id == to.UserId).ToDictionaryAsync(u => u.Id, ct);
        TransferRules.RequireCanTransact(users[from.UserId], "The sender");
        TransferRules.RequireCanTransact(users[to.UserId], "The recipient");

        var sourceParty = new TransferPartyDto("wallet", from.Id, from.UserId, from.Asset);
        var destinationParty = new TransferPartyDto("wallet", to.Id, to.UserId, to.Asset);

        if (request.DryRun == true)
        {
            var split = await SplitAsync(from, amount, ct);
            return new TransferPreviewDto(TransferKind.UserToUser, sourceParty, destinationParty, Amounts.Format(amount, from.Decimals),
                new PreviewEstimate([], Amounts.Format(amount, from.Decimals), IsPoints(from) ? Amounts.Format(split.Earned, from.Decimals) : null));
        }

        // The earned/cashable split is read before posting; if the sender spends at the same moment
        // the posting fails on the balance check, and is worked out again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PostUserToUserAsync(from, to, amount, request, idempotencyKey, sourceParty, destinationParty, ct);
            }
            catch (PaymentsException e) when (e.Error.Code == "insufficient_funds" && attempt < Attempts)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    async Task<TransferDto> PostUserToUserAsync(
        Wallet from, Wallet to, decimal amount, TransferCreateRequest request, string? idempotencyKey,
        TransferPartyDto sourceParty, TransferPartyDto destinationParty, CancellationToken ct)
    {
        var split = await SplitAsync(from, amount, ct);
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.WalletId == from.Id || a.WalletId == to.Id)
            .ToDictionaryAsync(a => (a.WalletId!, a.Bucket), a => a.Id, ct);

        var lines = new List<LedgerLine>();
        if (split.Earned > 0)
        {
            lines.Add(new(accounts[(from.Id, LedgerBucket.Earned)], -split.Earned));
            lines.Add(new(accounts[(to.Id, LedgerBucket.Earned)], split.Earned));
        }
        if (split.Cashable > 0)
        {
            lines.Add(new(accounts[(from.Id, LedgerBucket.Available)], -split.Cashable));
            lines.Add(new(accounts[(to.Id, LedgerBucket.Available)], split.Cashable));
        }

        var now = clock.GetUtcNow();
        var transfer = new Transfer
        {
            Id = Ids.New(Ids.Transfer, clock),
            Kind = TransferKind.UserToUser,
            State = TransferStates.Initial(TransferKind.UserToUser),
            SourceType = "wallet",
            SourceWalletId = from.Id,
            SourceUserId = from.UserId,
            DestinationType = "wallet",
            DestinationWalletId = to.Id,
            DestinationUserId = to.UserId,
            Asset = from.Asset,
            Amount = amount,
            IntegratorReference = request.IntegratorReference,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? [], PaymentsJson.Options),
            IdempotencyKey = idempotencyKey,
            CreatedAt = now,
            UpdatedAt = now
        };
        Move(transfer, TransferState.Completed);
        var formatted = Amounts.Format(amount, from.Decimals);
        transfer.ReceiptJson = JsonSerializer.Serialize(new ReceiptDto(
            formatted, formatted, IsPoints(from) ? Amounts.Format(split.Earned, from.Decimals) : null, [], now, now), PaymentsJson.Options);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Transfers.Add(transfer);
        var dto = To(transfer, from.Decimals);
        await events.AddAsync(EventTypes.TransferCreated, "transfer", transfer.Id, transfer.Version, dto, null, ct);
        // The posting saves the transfer and its event with it, in this transaction (SRV-LED-04, SC-04).
        await ledger.PostAsync(new PostingRequest($"Transfer {transfer.Id}", lines, transfer.Id, idempotencyKey), ct);
        await transaction.CommitAsync(ct);
        return dto;
    }

    /// <summary>How much of <paramref name="amount"/> comes from earned points (spent first) and how much from cashable ones.</summary>
    async Task<(decimal Earned, decimal Cashable)> SplitAsync(Wallet from, decimal amount, CancellationToken ct)
    {
        var balances = await db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId == from.Id)
            .ToDictionaryAsync(a => a.Bucket, a => a.Balance, ct);
        var earned = Math.Min(balances.GetValueOrDefault(LedgerBucket.Earned), amount);
        if (balances.GetValueOrDefault(LedgerBucket.Available) + earned < amount)
            throw PaymentsException.Unprocessable("insufficient_funds", "The available balance is too low for this transfer.", "amount");
        return (earned, amount - earned);
    }

    public async Task<TransferDto> GetAsync(string id, CancellationToken ct)
    {
        var transfer = await db.Transfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw PaymentsException.NotFound("transfer");
        return To(transfer, DecimalsOf(transfer.Asset));
    }

    public Task<Page<TransferDto>> ListAsync(
        PageRequest page, string? userId, string? walletId, TransferState? state, TransferKind? kind, string? integratorReference,
        DateTimeOffset? createdAfter, DateTimeOffset? createdBefore, CancellationToken ct)
    {
        var query = db.Transfers.AsNoTracking();
        if (userId is not null)
            query = query.Where(t => t.SourceUserId == userId || t.DestinationUserId == userId);
        if (walletId is not null)
            query = query.Where(t => t.SourceWalletId == walletId || t.DestinationWalletId == walletId);
        if (state is not null)
            query = query.Where(t => t.State == state);
        if (kind is not null)
            query = query.Where(t => t.Kind == kind);
        if (integratorReference is not null)
            query = query.Where(t => t.IntegratorReference == integratorReference);
        if (createdAfter is { } after)
            query = query.Where(t => t.CreatedAt > after);
        if (createdBefore is { } before)
            query = query.Where(t => t.CreatedAt < before);
        return query.PageAsync(t => t.Id, page, t => To(t, DecimalsOf(t.Asset)), ct);
    }

    /// <summary>Changes state only along the documented paths (FR-XFER-04).</summary>
    static void Move(Transfer transfer, TransferState to)
    {
        if (!TransferStates.CanMove(transfer.Kind, transfer.State, to))
            throw new InvalidOperationException($"A {transfer.Kind} transfer cannot move from {transfer.State} to {to}.");
        transfer.State = to;
    }

    async Task<Wallet> WalletAsync(string id, string field, CancellationToken ct) =>
        await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new PaymentsException(404, new ApiError("not_found", "No such wallet.", field));

    bool IsPoints(Wallet wallet) => wallet.Asset == options.Value.Points.Asset;

    int DecimalsOf(string asset) => asset == options.Value.Coin.Asset ? options.Value.Coin.Decimals : options.Value.Points.Decimals;

    static (TransferSourceRequest Source, TransferDestinationRequest Destination, decimal Amount) ValidateShape(TransferCreateRequest r)
    {
        var errors = new List<ApiErrorDetail>();
        if (r.Source is null)
            errors.Add(new("source", "required", "source is required."));
        else if (r.Source.Type is not ("wallet" or "fiat"))
            errors.Add(new("source.type", "invalid_value", "source.type is wallet or fiat."));
        else if (r.Source.Type == "wallet" && string.IsNullOrEmpty(r.Source.WalletId))
            errors.Add(new("source.wallet_id", "required", "source.wallet_id is required."));

        if (r.Destination is null)
            errors.Add(new("destination", "required", "destination is required."));
        else if (r.Destination.Type is not ("wallet" or "payout_account"))
            errors.Add(new("destination.type", "invalid_value", "destination.type is wallet or payout_account."));
        else if (r.Destination.Type == "wallet" && string.IsNullOrEmpty(r.Destination.WalletId))
            errors.Add(new("destination.wallet_id", "required", "destination.wallet_id is required."));

        if (r.Amount is null)
            errors.Add(new("amount", "required", "amount is required."));
        if (r.IntegratorReference is { Length: > 128 })
            errors.Add(new("integrator_reference", "too_long", "integrator_reference is at most 128 characters."));
        UserService.ValidateMetadata(r.Metadata, errors);

        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);
        return (r.Source!, r.Destination!, r.Amount!.Value);
    }

    static TransferDto To(Transfer t, int decimals) => new(
        t.Id, t.Kind, t.State,
        t.StateReasonJson is null ? null : JsonSerializer.Deserialize<ReasonDto>(t.StateReasonJson, PaymentsJson.Options),
        new TransferPartyDto(t.SourceType, t.SourceWalletId, t.SourceUserId, t.Asset),
        new TransferPartyDto(t.DestinationType, t.DestinationWalletId, t.DestinationUserId, t.Asset),
        Amounts.Format(t.Amount, decimals),
        t.ReceiptJson is null ? null : JsonSerializer.Deserialize<ReceiptDto>(t.ReceiptJson, PaymentsJson.Options),
        t.IntegratorReference,
        JsonSerializer.Deserialize<Dictionary<string, string>>(t.MetadataJson, PaymentsJson.Options) ?? [],
        t.Version, t.CreatedAt, t.UpdatedAt);
}
