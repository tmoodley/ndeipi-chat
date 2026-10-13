using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Providers;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Users;
using Ndeipi.Payments.Webhooks;

namespace Ndeipi.Payments.Ramps;

public sealed record DepositAccountSourceRequest(string? Currency, string? Rail);

public sealed record DepositAccountDestinationRequest(string? WalletId);

public sealed record DepositAccountCreateRequest(DepositAccountSourceRequest? Source, DepositAccountDestinationRequest? Destination, Dictionary<string, string>? Metadata);

/// <summary>The <c>DepositAccount</c> object (openapi.yaml).</summary>
public sealed record DepositAccountDto(
    string Id, string UserId, DepositAccountStatus Status, JsonElement Source, JsonElement Destination,
    JsonElement DepositInstructions, IReadOnlyDictionary<string, string> Metadata, int Version, DateTimeOffset CreatedAt)
{
    public string Object => "deposit_account";
}

/// <summary>The <c>Deposit</c> object (openapi.yaml): one payment into a standing deposit account.</summary>
public sealed record DepositDto(
    string Id, string DepositAccountId, string TransferId, TransferState State, string AmountReceived, string Currency,
    IReadOnlyList<FeeDto> Fees, string? Rate, string? AmountCredited, string? RailReference, ReasonDto? ReturnReason, DateTimeOffset CreatedAt)
{
    public string Object => "deposit";
}

/// <summary>What the sandbox or a provider reports arriving.</summary>
public enum DepositOutcome { Credited, InReview, Returned }

/// <summary>
/// On-ramps (SRS §4.4): fiat paid into Ndeipi's account on a rail becomes Ndeipi Points at the
/// fixed price. Either through a standing deposit account on a bank rail (every deposit creates an
/// on-ramp transfer), or a one-off on-ramp transfer with deposit instructions (bank details, or a
/// PayPal approval link).
///
/// When money arrives, one posting moves the fiat from the provider's clearing account into the
/// points reserve and credits purchased points to the wallet against the integrator's PointsIssued
/// account, so purchased points are always backed 1:1. Whatever arrives is credited, unless it is
/// below the route minimum, in which case it is returned (README decision 6).
/// </summary>
public sealed class OnRampService(
    PaymentsDbContext db,
    LedgerService ledger,
    HouseAccounts house,
    PointsIssuance issuance,
    TransferBook book,
    EventOutbox events,
    ProviderRegistry providers,
    RampCatalog catalog,
    PointsPricing pricing,
    IOptions<PaymentsOptions> options,
    TimeProvider clock)
{
    string Points => options.Value.Points.Asset;

    // ------------------------------------------------------------- Standing deposit accounts

    public async Task<DepositAccountDto> CreateAccountAsync(string userId, DepositAccountCreateRequest r, CancellationToken ct)
    {
        var errors = new List<ApiErrorDetail>();
        if (string.IsNullOrEmpty(r.Source?.Currency)) errors.Add(new("source.currency", "required", "source.currency is required."));
        if (string.IsNullOrEmpty(r.Source?.Rail)) errors.Add(new("source.rail", "required", "source.rail is required."));
        if (string.IsNullOrEmpty(r.Destination?.WalletId)) errors.Add(new("destination.wallet_id", "required", "destination.wallet_id is required."));
        UserService.ValidateMetadata(r.Metadata, errors);
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);

        var (currency, rail) = (r.Source!.Currency!, r.Source.Rail!);
        var terms = catalog.RailFor(rail, currency, "source.currency");
        if (!terms.StandingDeposits)
            throw PaymentsException.Unprocessable("unsupported_route",
                $"The {rail} rail takes one payment at a time; create a one-off on-ramp with POST /v1/transfers instead.", "source.rail");

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw PaymentsException.NotFound("user");
        TransferRules.RequireCanTransact(user, "This user");
        var wallet = await PointsWalletAsync(r.Destination!.WalletId!, userId, "destination.wallet_id", ct);

        var id = Ids.New(Ids.DepositAccount, clock);
        var reference = id[^10..];
        var collection = await CollectAsync(providers.RailFor(rail), new FiatCollectionRequest(reference, rail, currency, null, user.Email, null), ct);

        var account = new DepositAccount
        {
            Id = id,
            UserId = userId,
            WalletId = wallet.Id,
            Currency = currency,
            Rail = rail,
            Status = DepositAccountStatus.Active,
            ProviderReference = collection.ProviderReference,
            Reference = reference,
            InstructionsJson = Instructions(collection, currency, rail, amount: null, expiresAt: null).ToJsonString(),
            MetadataJson = JsonSerializer.Serialize(r.Metadata ?? [], PaymentsJson.Options),
            CreatedAt = clock.GetUtcNow()
        };
        db.DepositAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return To(account);
    }

    public async Task<DepositAccountDto> GetAccountAsync(string userId, string id, CancellationToken ct) => To(await AccountAsync(userId, id, tracked: false, ct));

    public async Task<Page<DepositAccountDto>> ListAccountsAsync(string userId, PageRequest page, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
            throw PaymentsException.NotFound("user");
        return await db.DepositAccounts.AsNoTracking().Where(d => d.UserId == userId).PageAsync(d => d.Id, page, To, ct);
    }

    /// <summary>Deactivates or reactivates (FR-ON-07). Deposits to a deactivated account are returned.</summary>
    public async Task<DepositAccountDto> SetActiveAsync(string userId, string id, bool active, CancellationToken ct)
    {
        var account = await AccountAsync(userId, id, tracked: true, ct);
        var status = active ? DepositAccountStatus.Active : DepositAccountStatus.Deactivated;
        if (account.Status == status)
            return To(account);
        if (active)
            TransferRules.RequireCanTransact(await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, ct), "This user");

        account.Status = status;
        account.Version++;
        if (!active)
            await events.AddAsync(EventTypes.DepositAccountDeactivated, "deposit_account", account.Id, account.Version, To(account), new { status = DepositAccountStatus.Active }, ct);
        await db.SaveChangesAsync(ct);
        return To(account);
    }

    public async Task<Page<DepositDto>> ActivityAsync(string userId, string id, PageRequest page, DateTimeOffset? after, DateTimeOffset? before, CancellationToken ct)
    {
        await AccountAsync(userId, id, tracked: false, ct);
        var deposits = db.Deposits.AsNoTracking().Where(d => d.DepositAccountId == id);
        if (after is { } a) deposits = deposits.Where(d => d.CreatedAt > a);
        if (before is { } b) deposits = deposits.Where(d => d.CreatedAt < b);
        var result = await deposits.PageAsync(d => d.Id, page, d => d, ct);

        var transferIds = result.Data.Select(d => d.TransferId).ToList();
        var transfers = await db.Transfers.AsNoTracking().Where(t => transferIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        return new Page<DepositDto>([.. result.Data.Select(d =>
        {
            var transfer = transfers[d.TransferId];
            var receipt = book.Receipt(transfer);
            return new DepositDto(d.Id, d.DepositAccountId, d.TransferId, transfer.State, book.Format(d.AmountReceived, d.Currency), d.Currency,
                receipt?.Fees ?? [], receipt?.Rate, receipt?.AmountCredited, d.RailReference,
                transfer.State == TransferState.Returned && transfer.StateReasonJson is { } reason ? JsonSerializer.Deserialize<ReasonDto>(reason, PaymentsJson.Options) : null,
                d.CreatedAt);
        })], result.HasMore);
    }

    // ------------------------------------------------------------- One-off on-ramps

    /// <summary>A transfer with a fiat source (FR-ON-02): returns <c>awaiting_funds</c> with how to pay.</summary>
    public async Task<object> CreateOneOffAsync(
        TransferSourceRequest source, Wallet wallet, decimal amount, TransferCreateRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var (currency, rail) = (source.Currency!, source.Rail!);
        var terms = catalog.RailFor(rail, currency, "source.currency");
        Amounts.RequireValid(amount, PointsPricing.FiatDecimals, currency);
        RampCatalog.RequireWithinLimits(terms, amount);
        if (wallet.Asset != Points)
            throw PaymentsException.Unprocessable("unsupported_route", "Fiat buys Ndeipi Points; convert to NdeipiCoin afterwards.", "destination.wallet_id");
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == wallet.UserId, ct);
        TransferRules.RequireCanTransact(user, "The recipient");

        var points = pricing.PointsFor(currency, amount);
        var destination = new TransferPartyDto("wallet", wallet.Id, wallet.UserId, Points);
        if (request.DryRun == true)
            return new TransferPreviewDto(TransferKind.Onramp, new TransferPartyDto("fiat", null, null, null, Currency: currency, Rail: rail), destination,
                book.Format(amount, currency),
                new PreviewEstimate([], book.Format(points, Points), null, Rate: Rate(currency), SettlementTimeSeconds: terms.SettlementSeconds));

        var transfer = book.New(TransferKind.Onramp, "fiat", "wallet", currency, amount, request, idempotencyKey);
        transfer.Rail = rail;
        transfer.DestinationAsset = Points;
        transfer.DestinationWalletId = wallet.Id;
        transfer.DestinationUserId = wallet.UserId;
        transfer.ExpiresAt = transfer.CreatedAt + terms.OneOffExpiry;

        var collection = await CollectAsync(providers.RailFor(rail),
            new FiatCollectionRequest(transfer.Id, rail, currency, amount, user.Email, null), ct);
        transfer.ProviderReference = collection.ProviderReference;
        transfer.DepositInstructionsJson = Instructions(collection, currency, rail, amount, transfer.ExpiresAt).ToJsonString();

        db.Transfers.Add(transfer);
        await book.CreatedAsync(transfer, ct);
        await db.SaveChangesAsync(ct);
        return book.To(transfer);
    }

    // ------------------------------------------------------------- Money arriving

    /// <summary>
    /// Fiat arrived for a standing deposit account: records the deposit, creates its on-ramp
    /// transfer, and credits it (FR-ON-04) or returns it (FR-ON-05).
    /// </summary>
    public async Task<TransferDto> ReceiveForAccountAsync(string depositAccountId, decimal amount, DepositOutcome outcome, string? railReference, CancellationToken ct)
    {
        var account = await db.DepositAccounts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == depositAccountId, ct)
            ?? throw new PaymentsException(404, new ApiError("not_found", "No such deposit account.", "deposit_account_id"));
        Amounts.RequireValid(amount, PointsPricing.FiatDecimals, account.Currency);

        var transfer = book.New(TransferKind.Onramp, "fiat", "wallet", account.Currency, amount, new TransferCreateRequest(null, null, amount, null, null, null, null), null);
        transfer.Rail = account.Rail;
        transfer.DestinationAsset = Points;
        transfer.DestinationWalletId = account.WalletId;
        transfer.DestinationUserId = account.UserId;
        transfer.DepositAccountId = account.Id;
        transfer.ProviderReference = account.ProviderReference;

        db.Transfers.Add(transfer);
        db.Deposits.Add(new Deposit
        {
            Id = Ids.New("dep", clock),
            DepositAccountId = account.Id,
            TransferId = transfer.Id,
            AmountReceived = amount,
            Currency = account.Currency,
            RailReference = railReference,
            CreatedAt = clock.GetUtcNow()
        });
        await book.CreatedAsync(transfer, ct);
        await db.SaveChangesAsync(ct);

        var refusal = account.Status == DepositAccountStatus.Deactivated
            ? new ReasonDto("deposit_account_deactivated", "The deposit account was deactivated, so the money was sent back.")
            : null;
        return await ApplyArrivalAsync(transfer.Id, amount, refusal is null ? outcome : DepositOutcome.Returned, railReference, refusal, ct);
    }

    /// <summary>Fiat arrived for a one-off on-ramp waiting for it.</summary>
    public async Task<TransferDto> ReceiveForTransferAsync(string transferId, decimal amount, DepositOutcome outcome, string? railReference, CancellationToken ct)
    {
        var transfer = await db.Transfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transferId, ct)
            ?? throw new PaymentsException(404, new ApiError("not_found", "No such transfer.", "transfer_id"));
        if (transfer.Kind != TransferKind.Onramp || transfer.State != TransferState.AwaitingFunds)
            throw PaymentsException.Conflict("invalid_state", "That transfer is not an on-ramp awaiting funds.");
        Amounts.RequireValid(amount, PointsPricing.FiatDecimals, transfer.Asset);
        return await ApplyArrivalAsync(transferId, amount, outcome, railReference, null, ct);
    }

    async Task<TransferDto> ApplyArrivalAsync(string transferId, decimal received, DepositOutcome outcome, string? railReference, ReasonDto? refusal, CancellationToken ct)
    {
        try
        {
            return await ApplyArrivalOnceAsync(transferId, received, outcome, railReference, refusal, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The instructions expired, or the same deposit was reported twice, at the same moment.
            throw PaymentsException.Conflict("invalid_state", "That on-ramp changed while the deposit was being applied.");
        }
    }

    async Task<TransferDto> ApplyArrivalOnceAsync(string transferId, decimal received, DepositOutcome outcome, string? railReference, ReasonDto? refusal, CancellationToken ct)
    {
        var current = await db.Transfers.AsNoTracking().SingleAsync(t => t.Id == transferId, ct);
        var currency = current.Asset;
        var terms = catalog.Rail(current.Rail!);
        var provider = providers.RailFor(current.Rail!).Name;
        var points = pricing.PointsFor(currency, received);
        if (refusal is null && received < terms.MinAmount)
            refusal = new ReasonDto("amount_below_minimum", $"{book.Format(received, currency)} {currency} is below the {terms.MinAmount} minimum, so it was sent back.");
        if (refusal is not null)
            outcome = DepositOutcome.Returned;

        // Open every account first: opening one saves, and nothing else may be pending when it does.
        var clearing = await house.ClearingAsync(provider, currency, ct);
        var reserve = await house.ReserveAsync(currency, ct);
        var suspense = await house.SuspenseAsync(currency, ct);
        var issued = await issuance.IssuedAccountAsync(LedgerBucket.Available, ct);
        var wallet = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.WalletId == current.DestinationWalletId && a.Bucket == LedgerBucket.Available).Select(a => a.Id).SingleAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var transfer = await TransferBook.LockAsync(db, transferId, TransferState.AwaitingFunds, ct);
        var now = clock.GetUtcNow();
        var receipt = new ReceiptDto(null, null, null, [], transfer.CreatedAt, null,
            AmountExpected: transfer.DepositAccountId is null ? book.Format(transfer.Amount, currency) : null,
            AmountReceived: book.Format(received, currency),
            Rate: Rate(currency),
            RailReference: railReference);

        if (outcome == DepositOutcome.Returned)
        {
            book.SetReceipt(transfer, receipt);
            await book.MoveAsync(transfer, TransferState.FundsReceived, null, ct);
            await book.MoveAsync(transfer, TransferState.Returned, refusal ?? new ReasonDto("returned", "The deposit was sent back."), ct, EventTypes.DepositReturned);
            await db.SaveChangesAsync(ct);
        }
        else if (outcome == DepositOutcome.InReview)
        {
            // Held in suspense until an operator releases or returns it (SRV-KYC-04).
            book.SetReceipt(transfer, receipt);
            await book.MoveAsync(transfer, TransferState.FundsReceived, null, ct, EventTypes.DepositReceived);
            await book.MoveAsync(transfer, TransferState.InReview, new ReasonDto("compliance_review", "The deposit is being reviewed."), ct);
            await ledger.PostAsync(new PostingRequest($"Deposit held for review, {transfer.Id}", [new(clearing, -received), new(suspense, received)], transfer.Id), ct);
        }
        else
        {
            var credited = book.Format(points, Points);
            book.SetReceipt(transfer, receipt with { AmountCredited = credited, CompletedAt = now });
            await book.MoveAsync(transfer, TransferState.FundsReceived, null, ct, EventTypes.DepositReceived);
            await book.MoveAsync(transfer, TransferState.Completed, null, ct);
            await ledger.PostAsync(new PostingRequest($"On-ramp {transfer.Id}",
                [new(clearing, -received), new(reserve, received), new(issued, -points), new(wallet, points)], transfer.Id), ct);
        }
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }

    /// <summary>
    /// An operator releases a deposit held in review (SRV-OPS-04): the fiat moves from suspense into
    /// the reserve and the points are credited, as for any deposit.
    /// </summary>
    public async Task<TransferDto> ReleaseHeldAsync(string transferId, CancellationToken ct)
    {
        var current = await db.Transfers.AsNoTracking().SingleAsync(t => t.Id == transferId, ct);
        var currency = current.Asset;
        var received = decimal.Parse(book.Receipt(current)!.AmountReceived!, System.Globalization.CultureInfo.InvariantCulture);
        var points = pricing.PointsFor(currency, received);
        var reserve = await house.ReserveAsync(currency, ct);
        var suspense = await house.SuspenseAsync(currency, ct);
        var issued = await issuance.IssuedAccountAsync(LedgerBucket.Available, ct);
        var wallet = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.WalletId == current.DestinationWalletId && a.Bucket == LedgerBucket.Available).Select(a => a.Id).SingleAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var transfer = await TransferBook.LockAsync(db, transferId, TransferState.InReview, ct);
        book.SetReceipt(transfer, book.Receipt(transfer)! with { AmountCredited = book.Format(points, Points), CompletedAt = clock.GetUtcNow() });
        transfer.StateReasonJson = null;
        await book.MoveAsync(transfer, TransferState.Completed, null, ct);
        await ledger.PostAsync(new PostingRequest($"Deposit released from review, {transfer.Id}",
            [new(suspense, -received), new(reserve, received), new(issued, -points), new(wallet, points)], transfer.Id), ct);
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }

    /// <summary>An operator rejects a deposit held in review: the money is sent back (FR-ON-05).</summary>
    public async Task<TransferDto> ReturnHeldAsync(string transferId, ReasonDto reason, CancellationToken ct)
    {
        var current = await db.Transfers.AsNoTracking().SingleAsync(t => t.Id == transferId, ct);
        var currency = current.Asset;
        var received = decimal.Parse(book.Receipt(current)!.AmountReceived!, System.Globalization.CultureInfo.InvariantCulture);
        var clearing = await house.ClearingAsync(providers.RailFor(current.Rail!).Name, currency, ct);
        var suspense = await house.SuspenseAsync(currency, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var transfer = await TransferBook.LockAsync(db, transferId, TransferState.InReview, ct);
        await book.MoveAsync(transfer, TransferState.Returned, reason, ct, EventTypes.DepositReturned);
        await ledger.PostAsync(new PostingRequest($"Deposit returned from review, {transfer.Id}",
            [new(suspense, -received), new(clearing, received)], transfer.Id), ct);
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }

    /// <summary>Cancels a one-off on-ramp whose money never came (run by the ramp worker).</summary>
    public async Task<bool> ExpireAsync(string transferId, CancellationToken ct)
    {
        var transfer = await db.Transfers.SingleAsync(t => t.Id == transferId, ct);
        if (transfer.State != TransferState.AwaitingFunds)
            return false;
        await book.MoveAsync(transfer, TransferState.Canceled, new ReasonDto("expired", "No money arrived before the deposit instructions expired."), ct);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // Money arrived at the same moment; that wins.
        }
    }

    // ------------------------------------------------------------- Helpers

    async Task<FiatCollection> CollectAsync(IFiatRail rail, FiatCollectionRequest request, CancellationToken ct)
    {
        try
        {
            return await rail.StartCollectionAsync(request, ct);
        }
        catch (ProviderUnavailableException e)
        {
            throw new PaymentsException(503, new ApiError("provider_unavailable", $"Deposits on this rail are unavailable right now. {e.Message}"));
        }
    }

    async Task<Wallet> PointsWalletAsync(string walletId, string userId, string field, CancellationToken ct)
    {
        var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == walletId && w.UserId == userId, ct)
            ?? throw new PaymentsException(404, new ApiError("not_found", "No such wallet for this user.", field));
        if (wallet.Asset != Points)
            throw PaymentsException.Unprocessable("unsupported_route", "Deposits credit Ndeipi Points wallets.", field);
        return wallet;
    }

    async Task<DepositAccount> AccountAsync(string userId, string id, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.DepositAccounts : db.DepositAccounts.AsNoTracking();
        return await query.FirstOrDefaultAsync(d => d.Id == id && d.UserId == userId, ct) ?? throw PaymentsException.NotFound("deposit account");
    }

    string Rate(string currency) => decimal.Round(1 / pricing.PriceIn(currency), 12, MidpointRounding.ToZero)
        .ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The contract's <c>DepositInstructions</c>: the rail's details plus type, currency, rail, and for one-offs the amount and expiry.</summary>
    JsonObject Instructions(FiatCollection collection, string currency, string rail, decimal? amount, DateTimeOffset? expiresAt)
    {
        var json = new JsonObject { ["type"] = collection.Type, ["currency"] = currency, ["rail"] = rail };
        foreach (var (key, value) in collection.Details)
            json[key] = value;
        if (amount is { } a)
            json["amount"] = book.Format(a, currency);
        if (expiresAt is { } e)
            json["expires_at"] = e;
        return json;
    }

    DepositAccountDto To(DepositAccount d) => new(
        d.Id, d.UserId, d.Status,
        JsonSerializer.SerializeToElement(new { currency = d.Currency, rail = d.Rail }),
        JsonSerializer.SerializeToElement(new { wallet_id = d.WalletId, asset = Points }),
        JsonDocument.Parse(d.InstructionsJson).RootElement.Clone(),
        JsonSerializer.Deserialize<Dictionary<string, string>>(d.MetadataJson, PaymentsJson.Options) ?? [],
        d.Version, d.CreatedAt);
}
