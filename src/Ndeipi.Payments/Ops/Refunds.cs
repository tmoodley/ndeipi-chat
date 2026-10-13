using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Ramps;
using Ndeipi.Payments.Transfers;

namespace Ndeipi.Payments.Ops;

public sealed record RefundCreateRequest(string? TransferId, decimal? Amount, string? Reason);

public sealed record RefundDto(
    string Id, string TransferId, Guid IntegratorId, string Amount, string Reason, RefundStatus Status,
    string RequestedBy, DateTimeOffset RequestedAt, string? DecidedBy, DateTimeOffset? DecidedAt);

/// <summary>
/// Manual refunds of completed cash-outs (SRV-OPS-04), for when a payout went wrong after the rail
/// accepted it. Four eyes: one operator requests, a different operator approves or declines, and the
/// database refuses the same name in both places. Approval credits the points back as cashable,
/// paid for by Ndeipi's treasury: the treasury's fiat goes into the reserve to back them, so the
/// reserve still covers every purchased point. A refund never exceeds what the cash-out paid, less
/// what earlier refunds of it returned.
/// </summary>
public sealed class RefundService(
    PaymentsDbContext db, IntegratorScope scope, LedgerService ledger, HouseAccounts house, PointsIssuance issuance,
    PointsPricing pricing, TransferBook book, TimeProvider clock)
{
    public async Task<RefundDto> RequestAsync(RefundCreateRequest r, string @operator, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(r.TransferId) || r.Amount is null || string.IsNullOrWhiteSpace(r.Reason))
            throw PaymentsException.Validation([new("transfer_id", "required", "transfer_id, amount and reason are all required.")]);
        var transfer = await db.Transfers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.TransferId, ct)
            ?? throw PaymentsException.NotFound("transfer");
        if (transfer.Kind != TransferKind.Offramp || transfer.State != TransferState.Completed)
            throw PaymentsException.Conflict("invalid_state", "Only a completed cash-out can be refunded by hand.");
        Amounts.RequireValid(r.Amount.Value, book.DecimalsOf(transfer.Asset), transfer.Asset);
        await RequireRefundableAsync(transfer, r.Amount.Value, ct);

        var refund = new RefundRequest
        {
            Id = Ids.New("rf", clock),
            IntegratorId = transfer.IntegratorId,
            TransferId = transfer.Id,
            Amount = r.Amount.Value,
            Reason = r.Reason.Trim(),
            Status = RefundStatus.Requested,
            RequestedBy = @operator,
            RequestedAt = clock.GetUtcNow()
        };
        db.RefundRequests.Add(refund);
        await db.SaveChangesAsync(ct);
        return To(refund, transfer.Asset);
    }

    public async Task<IReadOnlyList<RefundDto>> ListAsync(RefundStatus? status, CancellationToken ct)
    {
        var query = db.RefundRequests.AsNoTracking();
        if (status is not null)
            query = query.Where(r => r.Status == status);
        var refunds = await query.OrderByDescending(r => r.RequestedAt).Take(500).ToListAsync(ct);
        return [.. refunds.Select(r => To(r, null))];
    }

    public async Task<RefundDto> DecideAsync(string id, bool approve, string @operator, CancellationToken ct)
    {
        var refund = await db.RefundRequests.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw PaymentsException.NotFound("refund");
        if (refund.Status != RefundStatus.Requested)
            throw PaymentsException.Conflict("invalid_state", $"This refund was already {WireEnum.Name(refund.Status)}.");
        if (refund.RequestedBy == @operator)
            throw PaymentsException.Forbidden("four_eyes", "A refund must be decided by a different operator from the one who requested it.");

        var transfer = await db.Transfers.IgnoreQueryFilters().AsNoTracking().SingleAsync(t => t.Id == refund.TransferId, ct);
        refund.Status = approve ? RefundStatus.Approved : RefundStatus.Declined;
        refund.DecidedBy = @operator;
        refund.DecidedAt = clock.GetUtcNow();
        refund.Version++;

        if (!approve)
        {
            await db.SaveChangesAsync(ct);
            return To(refund, transfer.Asset);
        }

        // The posting is the integrator's: its user's wallet, and the treasury and reserve in the house.
        scope.Set(transfer.IntegratorId, apiKeyId: null);
        await RequireRefundableAsync(transfer, refund.Amount, ct);
        var currency = transfer.DestinationAsset!;
        var fiat = pricing.FiatFor(currency, refund.Amount);
        var treasury = await house.TreasuryAsync(currency, ct);
        var reserve = await house.ReserveAsync(currency, ct);
        var issued = await issuance.IssuedAccountAsync(LedgerBucket.Available, ct);
        var wallet = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.WalletId == transfer.SourceWalletId && a.Bucket == LedgerBucket.Available).Select(a => a.Id).SingleAsync(ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            // Saves the decision with the posting: approved and paid, or neither.
            await ledger.PostAsync(new PostingRequest($"Manual refund {refund.Id} of {transfer.Id}",
                [new(treasury, -fiat), new(reserve, fiat), new(issued, -refund.Amount), new(wallet, refund.Amount)], transfer.Id), ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw PaymentsException.Conflict("invalid_state", "This refund was decided at the same moment by someone else.");
        }
        await transaction.CommitAsync(ct);
        return To(refund, transfer.Asset);
    }

    async Task RequireRefundableAsync(Transfer transfer, decimal amount, CancellationToken ct)
    {
        var refunded = await db.RefundRequests.AsNoTracking()
            .Where(r => r.TransferId == transfer.Id && r.Status == RefundStatus.Approved)
            .SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        if (refunded + amount > transfer.Amount)
            throw PaymentsException.Unprocessable("amount_above_maximum",
                $"At most {book.Format(transfer.Amount - refunded, transfer.Asset)} of this cash-out is left to refund.", "amount");
    }

    RefundDto To(RefundRequest r, string? asset) => new(
        r.Id, r.TransferId, r.IntegratorId, asset is null ? r.Amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : book.Format(r.Amount, asset),
        r.Reason, r.Status, r.RequestedBy, r.RequestedAt, r.DecidedBy, r.DecidedAt);
}
