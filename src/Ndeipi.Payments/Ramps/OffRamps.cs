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

/// <summary>
/// Off-ramps (SRS §4.5): cashable points paid out as fiat to the user's own payout account, at the
/// fixed price (rounded down, so the reserve never pays more than it holds).
///
/// 1. <b>Accepted</b> (<c>pending</c>): one posting moves the points from available to pending and
///    sets the fiat aside from the reserve into suspense. The reserve may not go negative, so a
///    cash-out it cannot cover is refused with <c>insufficient_liquidity</c>.
/// 2. <b>Submitted</b> (<c>payout_submitted</c>): the ramp worker sends it to the rail. If the
///    provider is down it stays pending and is retried: queued, never dropped (SRV-PROV-07).
/// 3. <b>Completed</b>: the points are retired and the fiat leaves through the provider's clearing
///    account. <b>Returned or undeliverable</b>: both are reversed and the transfer ends
///    <c>refunded</c> (FR-OFF-05).
/// </summary>
public sealed class OffRampService(
    PaymentsDbContext db,
    LedgerService ledger,
    HouseAccounts house,
    PointsIssuance issuance,
    TransferBook book,
    ProviderRegistry providers,
    RampCatalog catalog,
    PointsPricing pricing,
    PayoutAccountService payoutAccounts,
    TransactionMonitor monitor,
    IOptions<PaymentsOptions> options,
    TimeProvider clock)
{
    string Points => options.Value.Points.Asset;

    public async Task<object> CreateAsync(Wallet from, string payoutAccountId, decimal amount, TransferCreateRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (from.Asset != Points)
            throw PaymentsException.Unprocessable("unsupported_route", "Only Ndeipi Points are cashed out; convert NdeipiCoin to points first.", "source.wallet_id");
        Amounts.RequireValid(amount, from.Decimals, from.Asset);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == from.UserId, ct);
        TransferRules.RequireCanTransact(user, "The sender");
        var payout = await db.PayoutAccounts.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == payoutAccountId && p.UserId == from.UserId && p.Status == PayoutAccountStatus.Active, ct)
            ?? throw new PaymentsException(404, new ApiError("not_found", "No such payout account for this user.", "destination.payout_account_id"));

        var terms = catalog.RailFor(payout.Rail, payout.Currency);
        var fiat = pricing.FiatFor(payout.Currency, amount);
        RampCatalog.RequireWithinLimits(terms, fiat);

        var buckets = await db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId == from.Id).ToDictionaryAsync(a => a.Bucket, ct);
        var cashable = buckets[LedgerBucket.Available].Balance;
        if (cashable + buckets[LedgerBucket.Earned].Balance < amount)
            throw PaymentsException.Unprocessable("insufficient_funds", "The available balance is too low for this transfer.", "amount");
        if (cashable < amount)
            throw PaymentsException.Unprocessable("amount_exceeds_cashable",
                $"Only {book.Format(cashable, Points)} of these points were bought and can be cashed out; earned points cannot.", "amount");

        var reserve = await house.ReserveAsync(payout.Currency, ct);
        if (await house.BalanceAsync(reserve, ct) < fiat)
            throw PaymentsException.Unprocessable("insufficient_liquidity", "Ndeipi cannot pay this out in that currency right now. Try a smaller amount later.", "amount");

        var source = new TransferPartyDto("wallet", from.Id, from.UserId, Points);
        var destination = new TransferPartyDto("payout_account", null, from.UserId, null, payout.Id, payout.Currency, payout.Rail);
        if (request.DryRun == true)
            return new TransferPreviewDto(TransferKind.Offramp, source, destination, book.Format(amount, Points),
                new PreviewEstimate([], null, null, AmountPaidOut: book.Format(fiat, payout.Currency), Rate: Rate(payout.Currency), SettlementTimeSeconds: terms.SettlementSeconds));

        var suspense = await house.SuspenseAsync(payout.Currency, ct);
        var transfer = book.New(TransferKind.Offramp, "wallet", "payout_account", Points, amount, request, idempotencyKey);
        transfer.SourceWalletId = from.Id;
        transfer.SourceUserId = from.UserId;
        transfer.DestinationUserId = from.UserId;
        transfer.PayoutAccountId = payout.Id;
        transfer.DestinationAsset = payout.Currency;
        transfer.Rail = payout.Rail;
        book.SetReceipt(transfer, new ReceiptDto(book.Format(amount, Points), null, null, [], transfer.CreatedAt, null,
            AmountPaidOut: book.Format(fiat, payout.Currency), Rate: Rate(payout.Currency)));
        // Held for review, the funds are set aside just the same; only the submission waits.
        if (await monitor.AssessAsync(from.UserId, Points, amount, ct) is { } hold)
        {
            TransferBook.MoveUnsaved(transfer, TransferState.InReview);
            transfer.StateReasonJson = System.Text.Json.JsonSerializer.Serialize(hold, PaymentsJson.Options);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Transfers.Add(transfer);
        await book.CreatedAsync(transfer, ct);
        await ledger.PostAsync(new PostingRequest($"Off-ramp accepted, {transfer.Id}",
        [
            new(buckets[LedgerBucket.Available].Id, -amount), new(buckets[LedgerBucket.Pending].Id, amount),
            new(reserve, -fiat), new(suspense, fiat)
        ], transfer.Id, idempotencyKey), ct);
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }

    /// <summary>
    /// Sends a pending off-ramp to its rail. The transfer's ID is the provider reference, so a
    /// repeated submission is recognised rather than paid twice (SRV-PROV-04). False if the provider
    /// is down: the transfer stays pending and is tried again.
    ///
    /// An off-ramp held in review is submitted only when an operator releases it
    /// (<paramref name="released"/>); if the provider is down then, it stays in review and the release
    /// answers <c>provider_unavailable</c> so the operator can try again.
    /// </summary>
    public async Task<bool> SubmitAsync(string transferId, CancellationToken ct, bool released = false)
    {
        var transfer = await db.Transfers.SingleAsync(t => t.Id == transferId, ct);
        var from = released ? TransferState.InReview : TransferState.Pending;
        if (transfer.Kind != TransferKind.Offramp || transfer.State != from)
            return false;
        var payout = await db.PayoutAccounts.AsNoTracking().SingleAsync(p => p.Id == transfer.PayoutAccountId, ct);
        var fiat = pricing.FiatFor(payout.Currency, transfer.Amount);

        ProviderOperation operation;
        try
        {
            operation = await providers.RailFor(payout.Rail).SubmitPayoutAsync(new FiatPayoutRequest(
                transfer.Id, payout.Rail, payout.Currency, fiat, payout.Country, payout.AccountOwnerName, payoutAccounts.Destination(payout)), ct);
        }
        catch (ProviderUnavailableException e) when (released)
        {
            throw new PaymentsException(503, new ApiError("provider_unavailable", $"The rail is unavailable, so the payout stays in review. {e.Message}"));
        }
        catch (ProviderUnavailableException)
        {
            return false;
        }

        transfer.ProviderReference = operation.ProviderReference;
        await book.MoveAsync(transfer, TransferState.PayoutSubmitted, null, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another worker submitted it; the provider saw the same reference. Drop the stale copy.
            db.ChangeTracker.Clear();
            return false;
        }
    }

    /// <summary>
    /// Applies what the rail reports for a submitted payout: from the provider's webhook or the
    /// worker's polling (SRV-PROV-05), or the sandbox (FR-SBX-02).
    /// </summary>
    public async Task<TransferDto> ApplyOutcomeAsync(string transferId, ProviderOperationStatus status, string? railReference, string? reason, CancellationToken ct)
    {
        try
        {
            return await ApplyOutcomeOnceAsync(transferId, status, railReference, reason, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The worker and a webhook (or the sandbox) reported it at the same moment; the other one applied it.
            throw PaymentsException.Conflict("invalid_state", "That payout's outcome has already been applied.");
        }
    }

    /// <summary>An operator rejects a cash-out held in review: the points and the reserved fiat go back.</summary>
    public async Task<TransferDto> RejectHeldAsync(string transferId, ReasonDto reason, CancellationToken ct)
    {
        var current = await db.Transfers.AsNoTracking().SingleAsync(t => t.Id == transferId, ct);
        var payout = await db.PayoutAccounts.AsNoTracking().SingleAsync(p => p.Id == current.PayoutAccountId, ct);
        var fiat = pricing.FiatFor(payout.Currency, current.Amount);
        var reserve = await house.ReserveAsync(payout.Currency, ct);
        var suspense = await house.SuspenseAsync(payout.Currency, ct);
        var wallet = await db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId == current.SourceWalletId).ToDictionaryAsync(a => a.Bucket, a => a.Id, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var transfer = await TransferBook.LockAsync(db, transferId, TransferState.InReview, ct);
        await book.MoveAsync(transfer, TransferState.Refunded, reason, ct);
        await ledger.PostAsync(new PostingRequest($"Off-ramp rejected in review, {transfer.Id}",
        [
            new(wallet[LedgerBucket.Pending], -transfer.Amount), new(wallet[LedgerBucket.Available], transfer.Amount),
            new(suspense, -fiat), new(reserve, fiat)
        ], transfer.Id), ct);
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }

    async Task<TransferDto> ApplyOutcomeOnceAsync(string transferId, ProviderOperationStatus status, string? railReference, string? reason, CancellationToken ct)
    {
        var current = await db.Transfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == transferId, ct) ?? throw PaymentsException.NotFound("transfer");
        if (current.Kind != TransferKind.Offramp || current.State != TransferState.PayoutSubmitted)
            throw PaymentsException.Conflict("invalid_state", "That transfer is not an off-ramp waiting on its payout.");
        if (status is not (ProviderOperationStatus.Completed or ProviderOperationStatus.Returned or ProviderOperationStatus.Undeliverable))
            return book.To(current);

        var payout = await db.PayoutAccounts.AsNoTracking().SingleAsync(p => p.Id == current.PayoutAccountId, ct);
        var fiat = pricing.FiatFor(payout.Currency, current.Amount);
        var provider = providers.RailFor(payout.Rail).Name;
        var clearing = await house.ClearingAsync(provider, payout.Currency, ct);
        var reserve = await house.ReserveAsync(payout.Currency, ct);
        var suspense = await house.SuspenseAsync(payout.Currency, ct);
        var issued = await issuance.IssuedAccountAsync(LedgerBucket.Available, ct);
        var wallet = await db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId == current.SourceWalletId).ToDictionaryAsync(a => a.Bucket, a => a.Id, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var transfer = await TransferBook.LockAsync(db, transferId, TransferState.PayoutSubmitted, ct);
        var receipt = book.Receipt(transfer)!;
        if (status == ProviderOperationStatus.Completed)
        {
            book.SetReceipt(transfer, receipt with { RailReference = railReference, CompletedAt = clock.GetUtcNow() });
            await book.MoveAsync(transfer, TransferState.Completed, null, ct);
            await ledger.PostAsync(new PostingRequest($"Off-ramp paid out, {transfer.Id}",
            [
                new(wallet[LedgerBucket.Pending], -transfer.Amount), new(issued, transfer.Amount),
                new(suspense, -fiat), new(clearing, fiat)
            ], transfer.Id), ct);
        }
        else
        {
            var failed = status == ProviderOperationStatus.Returned ? TransferState.Returned : TransferState.Undeliverable;
            var why = new ReasonDto(failed == TransferState.Returned ? "payout_returned" : "payout_undeliverable",
                reason ?? "The rail could not pay this account, so the points were refunded.");
            book.SetReceipt(transfer, receipt with { RailReference = railReference });
            await book.MoveAsync(transfer, failed, why, ct, EventTypes.PayoutReturned);
            await book.MoveAsync(transfer, TransferState.Refunded, why, ct);
            await ledger.PostAsync(new PostingRequest($"Off-ramp refunded, {transfer.Id}",
            [
                new(wallet[LedgerBucket.Pending], -transfer.Amount), new(wallet[LedgerBucket.Available], transfer.Amount),
                new(suspense, -fiat), new(reserve, fiat)
            ], transfer.Id), ct);
        }
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }

    string Rate(string currency) => pricing.PriceIn(currency).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
}
