using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Ramps;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Users;

namespace Ndeipi.Payments.Ops;

public sealed record ReviewItemDto(string TransferId, Guid IntegratorId, TransferKind Kind, string Amount, string Asset, ReasonDto? Reason, string? SourceUserId, string? DestinationUserId, DateTimeOffset CreatedAt);

/// <summary>
/// What operators review and decide (SRV-OPS-04, SRV-KYC-04): every transfer held in
/// <c>in_review</c>, across integrators, and the release or rejection of each. A decision acts as
/// the transfer's integrator, so its events and postings are that integrator's, and the audit log
/// records which operator made it.
/// </summary>
public sealed class ReviewService(
    PaymentsDbContext db, IntegratorScope scope, LedgerService ledger, TransferBook book, OnRampService onRamps, OffRampService offRamps, TimeProvider clock)
{
    public async Task<IReadOnlyList<ReviewItemDto>> HeldAsync(CancellationToken ct) =>
        [.. (await db.Transfers.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.State == TransferState.InReview)
            .OrderBy(t => t.CreatedAt)
            .Take(500)
            .ToListAsync(ct))
            .Select(t => new ReviewItemDto(t.Id, t.IntegratorId, t.Kind, book.Format(t.Amount, t.Asset), t.Asset,
                book.To(t).StateReason, t.SourceUserId, t.DestinationUserId, t.CreatedAt))];

    public async Task<TransferDto> ReleaseAsync(string transferId, CancellationToken ct)
    {
        var transfer = await ActAsAsync(transferId, ct);
        return transfer.Kind switch
        {
            TransferKind.UserToUser => await SettleUserToUserAsync(transferId, release: true, null, ct),
            TransferKind.Onramp => await onRamps.ReleaseHeldAsync(transferId, ct),
            TransferKind.Offramp => await ReleaseOffRampAsync(transferId, ct),
            _ => throw PaymentsException.Conflict("invalid_state", "Only transfers, deposits and cash-outs are held for review.")
        };
    }

    public async Task<TransferDto> RejectAsync(string transferId, string reason, CancellationToken ct)
    {
        var why = new ReasonDto("rejected_in_review", reason);
        var transfer = await ActAsAsync(transferId, ct);
        return transfer.Kind switch
        {
            TransferKind.UserToUser => await SettleUserToUserAsync(transferId, release: false, why, ct),
            TransferKind.Onramp => await onRamps.ReturnHeldAsync(transferId, why, ct),
            TransferKind.Offramp => await offRamps.RejectHeldAsync(transferId, why, ct),
            _ => throw PaymentsException.Conflict("invalid_state", "Only transfers, deposits and cash-outs are held for review.")
        };
    }

    /// <summary>Finds the held transfer and takes on its integrator's scope for the decision.</summary>
    async Task<Transfer> ActAsAsync(string transferId, CancellationToken ct)
    {
        var transfer = await db.Transfers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(t => t.Id == transferId, ct)
            ?? throw PaymentsException.NotFound("transfer");
        if (transfer.State != TransferState.InReview)
            throw PaymentsException.Conflict("invalid_state", $"That transfer is {WireEnum.Name(transfer.State)}, not in review.");
        scope.Set(transfer.IntegratorId, apiKeyId: null);
        return transfer;
    }

    async Task<TransferDto> ReleaseOffRampAsync(string transferId, CancellationToken ct)
    {
        if (!await offRamps.SubmitAsync(transferId, ct, released: true))
            throw PaymentsException.Conflict("invalid_state", "That cash-out was decided at the same moment by someone else.");
        return book.To(await db.Transfers.AsNoTracking().SingleAsync(t => t.Id == transferId, ct));
    }

    /// <summary>
    /// A held user-to-user transfer waits in the sender's pending bucket. Released, it goes to the
    /// recipient; rejected, back to the sender. Either way each kind of point lands where it came
    /// from: the receipt's earned amount as earned, the rest as cashable.
    /// </summary>
    async Task<TransferDto> SettleUserToUserAsync(string transferId, bool release, ReasonDto? reason, CancellationToken ct)
    {
        var current = await db.Transfers.AsNoTracking().SingleAsync(t => t.Id == transferId, ct);
        var receipt = book.Receipt(current)!;
        var earned = receipt.EarnedAmount is { } e ? decimal.Parse(e, System.Globalization.CultureInfo.InvariantCulture) : 0m;
        var cashable = current.Amount - earned;
        var target = release ? current.DestinationWalletId! : current.SourceWalletId!;
        var accounts = await db.LedgerAccounts.AsNoTracking()
            .Where(a => a.WalletId == current.SourceWalletId || a.WalletId == target)
            .ToListAsync(ct);
        string Account(string wallet, LedgerBucket bucket) => accounts.Single(a => a.WalletId == wallet && a.Bucket == bucket).Id;

        var lines = new List<LedgerLine> { new(Account(current.SourceWalletId!, LedgerBucket.Pending), -current.Amount) };
        if (earned > 0)
            lines.Add(new(Account(target, LedgerBucket.Earned), earned));
        if (cashable > 0)
            lines.Add(new(Account(target, LedgerBucket.Available), cashable));

        if (release)
        {
            var recipient = await db.Users.AsNoTracking().SingleAsync(u => u.Id == current.DestinationUserId, ct);
            TransferRules.RequireCanTransact(recipient, "The recipient");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var transfer = await TransferBook.LockAsync(db, transferId, TransferState.InReview, ct);
        if (release)
        {
            book.SetReceipt(transfer, receipt with { AmountCredited = receipt.AmountDebited, CompletedAt = clock.GetUtcNow() });
            transfer.StateReasonJson = null;
            await book.MoveAsync(transfer, TransferState.Completed, null, ct);
        }
        else
            await book.MoveAsync(transfer, TransferState.Failed, reason, ct);
        await ledger.PostAsync(new PostingRequest($"Review {(release ? "released" : "rejected")}, {transfer.Id}", lines, transfer.Id), ct);
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }
}
