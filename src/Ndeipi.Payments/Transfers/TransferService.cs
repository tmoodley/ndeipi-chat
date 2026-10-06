using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Ramps;
using Ndeipi.Payments.Treasury;
using Ndeipi.Payments.Users;

namespace Ndeipi.Payments.Transfers;

/// <summary>
/// Transfers (SRS §4.3 to §4.5): one resource for every kind (DC-04), routed by source and
/// destination. User to user is handled here; conversions, on-ramps and off-ramps by their own
/// services. Points keep their kind in user-to-user transfers: the sender's earned points go first
/// and arrive as earned points, so a reward never becomes cashable by changing hands.
/// </summary>
public sealed class TransferService(
    PaymentsDbContext db,
    LedgerService ledger,
    TransferBook book,
    OnRampService onRamps,
    OffRampService offRamps,
    ConversionService conversions,
    IOptions<PaymentsOptions> options)
{
    const int Attempts = 3;

    public async Task<object> CreateAsync(TransferCreateRequest request, string? idempotencyKey, CancellationToken ct)
    {
        var (source, destination, amount) = ValidateShape(request);

        if (source.Type == "fiat")
        {
            if (destination.Type != "wallet")
                throw PaymentsException.Unprocessable("unsupported_route", "Fiat can only be paid into a wallet.", "destination.type");
            return await onRamps.CreateOneOffAsync(source, await WalletAsync(destination.WalletId!, "destination.wallet_id", ct), amount, request, idempotencyKey, ct);
        }

        var from = await WalletAsync(source.WalletId!, "source.wallet_id", ct);
        if (destination.Type == "payout_account")
            return await offRamps.CreateAsync(from, destination.PayoutAccountId!, amount, request, idempotencyKey, ct);

        var to = await WalletAsync(destination.WalletId!, "destination.wallet_id", ct);
        if (from.Id == to.Id)
            throw PaymentsException.Unprocessable("same_source_and_destination", "The source and destination are the same wallet.", "destination.wallet_id");
        if (from.Asset != to.Asset)
        {
            if (from.UserId == to.UserId)
                return await conversions.CreateAsync(from, to, amount, request, idempotencyKey, ct);
            throw PaymentsException.Unprocessable("asset_mismatch", $"The wallets hold {from.Asset} and {to.Asset}.", "destination.wallet_id");
        }
        if (request.QuoteId is not null)
            throw PaymentsException.Validation([new("quote_id", "not_allowed", "quote_id is only for conversions.")]);
        return await UserToUserAsync(from, to, amount, request, idempotencyKey, ct);
    }

    async Task<object> UserToUserAsync(Wallet from, Wallet to, decimal amount, TransferCreateRequest request, string? idempotencyKey, CancellationToken ct)
    {
        Amounts.RequireValid(amount, from.Decimals, from.Asset);
        var users = await db.Users.AsNoTracking().Where(u => u.Id == from.UserId || u.Id == to.UserId).ToDictionaryAsync(u => u.Id, ct);
        TransferRules.RequireCanTransact(users[from.UserId], "The sender");
        TransferRules.RequireCanTransact(users[to.UserId], "The recipient");

        if (request.DryRun == true)
        {
            var split = await SplitAsync(from, amount, ct);
            return new TransferPreviewDto(TransferKind.UserToUser,
                new TransferPartyDto("wallet", from.Id, from.UserId, from.Asset), new TransferPartyDto("wallet", to.Id, to.UserId, to.Asset),
                book.Format(amount, from.Asset),
                new PreviewEstimate([], book.Format(amount, from.Asset), IsPoints(from) ? book.Format(split.Earned, from.Asset) : null));
        }

        // The earned/cashable split is read before posting; if the sender spends at the same moment
        // the posting fails on the balance check, and is worked out again.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await PostUserToUserAsync(from, to, amount, request, idempotencyKey, ct);
            }
            catch (PaymentsException e) when (e.Error.Code == "insufficient_funds" && attempt < Attempts)
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    async Task<TransferDto> PostUserToUserAsync(Wallet from, Wallet to, decimal amount, TransferCreateRequest request, string? idempotencyKey, CancellationToken ct)
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

        var transfer = book.New(TransferKind.UserToUser, "wallet", "wallet", from.Asset, amount, request, idempotencyKey);
        transfer.SourceWalletId = from.Id;
        transfer.SourceUserId = from.UserId;
        transfer.DestinationWalletId = to.Id;
        transfer.DestinationUserId = to.UserId;
        TransferBook.MoveUnsaved(transfer, TransferState.Completed);
        var formatted = book.Format(amount, from.Asset);
        book.SetReceipt(transfer, new ReceiptDto(formatted, formatted, IsPoints(from) ? book.Format(split.Earned, from.Asset) : null, [], transfer.CreatedAt, transfer.CreatedAt));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Transfers.Add(transfer);
        await book.CreatedAsync(transfer, ct);
        // The posting saves the transfer and its event with it, in this transaction (SRV-LED-04, SC-04).
        await ledger.PostAsync(new PostingRequest($"Transfer {transfer.Id}", lines, transfer.Id, idempotencyKey), ct);
        await transaction.CommitAsync(ct);
        return book.To(transfer);
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

    public async Task<TransferDto> GetAsync(string id, CancellationToken ct) =>
        book.To(await db.Transfers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw PaymentsException.NotFound("transfer"));

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
        return query.PageAsync(t => t.Id, page, book.To, ct);
    }

    async Task<Wallet> WalletAsync(string id, string field, CancellationToken ct) =>
        await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct)
            ?? throw new PaymentsException(404, new ApiError("not_found", "No such wallet.", field));

    bool IsPoints(Wallet wallet) => wallet.Asset == options.Value.Points.Asset;

    static (TransferSourceRequest Source, TransferDestinationRequest Destination, decimal Amount) ValidateShape(TransferCreateRequest r)
    {
        var errors = new List<ApiErrorDetail>();
        if (r.Source is null)
            errors.Add(new("source", "required", "source is required."));
        else if (r.Source.Type is not ("wallet" or "fiat"))
            errors.Add(new("source.type", "invalid_value", "source.type is wallet or fiat."));
        else if (r.Source.Type == "wallet" && string.IsNullOrEmpty(r.Source.WalletId))
            errors.Add(new("source.wallet_id", "required", "source.wallet_id is required."));
        else if (r.Source.Type == "fiat")
        {
            if (string.IsNullOrEmpty(r.Source.Currency)) errors.Add(new("source.currency", "required", "source.currency is required."));
            if (string.IsNullOrEmpty(r.Source.Rail)) errors.Add(new("source.rail", "required", "source.rail is required."));
        }

        if (r.Destination is null)
            errors.Add(new("destination", "required", "destination is required."));
        else if (r.Destination.Type is not ("wallet" or "payout_account"))
            errors.Add(new("destination.type", "invalid_value", "destination.type is wallet or payout_account."));
        else if (r.Destination.Type == "wallet" && string.IsNullOrEmpty(r.Destination.WalletId))
            errors.Add(new("destination.wallet_id", "required", "destination.wallet_id is required."));
        else if (r.Destination.Type == "payout_account" && string.IsNullOrEmpty(r.Destination.PayoutAccountId))
            errors.Add(new("destination.payout_account_id", "required", "destination.payout_account_id is required."));

        if (r.Amount is null)
            errors.Add(new("amount", "required", "amount is required."));
        if (r.IntegratorReference is { Length: > 128 })
            errors.Add(new("integrator_reference", "too_long", "integrator_reference is at most 128 characters."));
        UserService.ValidateMetadata(r.Metadata, errors);

        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);
        return (r.Source!, r.Destination!, r.Amount!.Value);
    }
}
