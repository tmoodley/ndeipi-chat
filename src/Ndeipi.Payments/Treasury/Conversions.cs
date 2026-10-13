using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Transfers;

namespace Ndeipi.Payments.Treasury;

public sealed record QuoteCreateRequest(string? SourceWalletId, string? DestinationWalletId, decimal? Amount);

/// <summary>The <c>Quote</c> object (openapi.yaml).</summary>
public sealed record QuoteDto(
    string Id, string SourceWalletId, string DestinationWalletId, string From, string To, string Amount, string Rate,
    string AmountOut, IReadOnlyList<FeeDto> Fees, string Status, string? TransferId, DateTimeOffset ExpiresAt, DateTimeOffset CreatedAt)
{
    public string Object => "quote";
}

/// <summary>
/// Locked prices for converting between a user's Ndeipi Points and NdeipiCoin wallets (FR-RATE-04),
/// from <see cref="CoinPricing"/>. Only cashable points can buy NdeipiCoin, so rewards cannot reach
/// cash through it. A quote larger than Ndeipi's stock (buying) or the treasury's points (selling)
/// is refused up front with <c>insufficient_liquidity</c>; the conversion checks again atomically.
/// </summary>
public sealed class QuoteService(
    PaymentsDbContext db, CoinPricing pricing, HouseAccounts house, TransferBook book, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    public async Task<QuoteDto> CreateAsync(QuoteCreateRequest r, CancellationToken ct)
    {
        var errors = new List<ApiErrorDetail>();
        if (string.IsNullOrEmpty(r.SourceWalletId)) errors.Add(new("source_wallet_id", "required", "source_wallet_id is required."));
        if (string.IsNullOrEmpty(r.DestinationWalletId)) errors.Add(new("destination_wallet_id", "required", "destination_wallet_id is required."));
        if (r.Amount is null) errors.Add(new("amount", "required", "amount is required."));
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);

        var (from, to) = await WalletsAsync(r.SourceWalletId!, r.DestinationWalletId!, ct);
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == from.UserId, ct);
        TransferRules.RequireCanTransact(user, "This user");

        var quote = await pricing.QuoteAsync(from.Asset, r.Amount!.Value, ct);
        await RequireLiquidityAsync(from, quote, ct);

        var now = clock.GetUtcNow();
        var row = new Quote
        {
            Id = Ids.New("qt", clock),
            UserId = from.UserId,
            SourceWalletId = from.Id,
            DestinationWalletId = to.Id,
            From = quote.From,
            To = quote.To,
            Amount = quote.Amount,
            Fee = quote.Fee,
            AmountOut = quote.AmountOut,
            Rate = quote.Rate,
            Status = QuoteStatus.Open,
            ExpiresAt = now + options.Value.Coin.QuoteLifetime,
            CreatedAt = now
        };
        db.Quotes.Add(row);
        await db.SaveChangesAsync(ct);
        return To(row);
    }

    public async Task<QuoteDto> GetAsync(string id, CancellationToken ct) =>
        To(await db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == id, ct) ?? throw PaymentsException.NotFound("quote"));

    /// <summary>The user's two wallets, one in each asset.</summary>
    internal async Task<(Wallet From, Wallet To)> WalletsAsync(string fromId, string toId, CancellationToken ct)
    {
        var wallets = await db.Wallets.AsNoTracking().Where(w => w.Id == fromId || w.Id == toId).ToDictionaryAsync(w => w.Id, ct);
        var from = wallets.GetValueOrDefault(fromId) ?? throw new PaymentsException(404, new ApiError("not_found", "No such wallet.", "source_wallet_id"));
        var to = wallets.GetValueOrDefault(toId) ?? throw new PaymentsException(404, new ApiError("not_found", "No such wallet.", "destination_wallet_id"));
        var assets = new[] { options.Value.Points.Asset, options.Value.Coin.Asset };
        if (from.UserId != to.UserId || from.Asset == to.Asset || !assets.Contains(from.Asset) || !assets.Contains(to.Asset))
            throw PaymentsException.Unprocessable("unsupported_route", "A conversion is between one user's points wallet and their NdeipiCoin wallet.", "destination_wallet_id");
        return (from, to);
    }

    /// <summary>
    /// Points buying NdeipiCoin must be cashable, and Ndeipi must hold what it will pay out: NdeipiCoin
    /// from its stock, or points from its treasury.
    /// </summary>
    internal async Task RequireLiquidityAsync(Wallet from, CoinQuote quote, CancellationToken ct)
    {
        if (from.Asset == options.Value.Points.Asset)
        {
            var buckets = await db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId == from.Id).ToDictionaryAsync(a => a.Bucket, a => a.Balance, ct);
            if (buckets.GetValueOrDefault(LedgerBucket.Available) + buckets.GetValueOrDefault(LedgerBucket.Earned) < quote.Amount)
                throw PaymentsException.Unprocessable("insufficient_funds", "The available balance is too low for this conversion.", "amount");
            if (buckets.GetValueOrDefault(LedgerBucket.Available) < quote.Amount)
                throw PaymentsException.Unprocessable("amount_exceeds_cashable", "Only bought points can buy NdeipiCoin; earned points cannot.", "amount");
        }
        var paying = from.Asset == options.Value.Points.Asset ? await house.InventoryAsync(quote.To, ct) : await house.TreasuryAsync(quote.To, ct);
        if (await house.BalanceAsync(paying, ct) < quote.AmountOut)
            throw PaymentsException.Unprocessable("insufficient_liquidity", "Ndeipi cannot cover this conversion right now. Try a smaller amount later.", "amount");
    }

    QuoteDto To(Quote q) => new(
        q.Id, q.SourceWalletId, q.DestinationWalletId, q.From, q.To, book.Format(q.Amount, q.From),
        q.Rate.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture),
        book.Format(q.AmountOut, q.To), [new FeeDto("ndeipi", book.Format(q.Fee, q.From), q.From)],
        q.Status == QuoteStatus.Used ? "used" : q.ExpiresAt <= clock.GetUtcNow() ? "expired" : "open",
        q.TransferId, q.ExpiresAt, q.CreatedAt);
}

/// <summary>
/// Conversions (FR-XFER-10): a transfer between one user's points and NdeipiCoin wallets at a locked
/// quote, settled from Ndeipi's own stock in one transaction, so it completes or fails at once.
///
/// Buying NdeipiCoin: the points move into the treasury (less the spread, which goes to fees) and
/// NdeipiCoin comes out of the inventory. Selling runs the other way, and the points received are
/// cashable. The points reserve is not touched: the points still exist, Ndeipi now holds them. If
/// the inventory or the treasury runs short, the posting fails with <c>insufficient_liquidity</c>.
/// </summary>
public sealed class ConversionService(
    PaymentsDbContext db, LedgerService ledger, HouseAccounts house, QuoteService quotes, TransferBook book, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    public async Task<object> CreateAsync(Wallet from, Wallet to, decimal amount, TransferCreateRequest request, string? idempotencyKey, CancellationToken ct)
    {
        if (request.QuoteId is null)
            throw PaymentsException.Unprocessable("quote_required", "A conversion needs a quote_id from POST /v1/quotes.", "quote_id");
        var quote = await db.Quotes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == request.QuoteId, ct)
            ?? throw new PaymentsException(404, new ApiError("not_found", "No such quote.", "quote_id"));
        if (quote.SourceWalletId != from.Id || quote.DestinationWalletId != to.Id || quote.Amount != amount)
            throw PaymentsException.Unprocessable("quote_mismatch", "The quote's wallets or amount differ from this transfer.", "quote_id");
        if (quote.Status == QuoteStatus.Used)
            throw PaymentsException.Conflict("quote_used", "This quote has already settled a conversion.", quote.TransferId, "quote_id");
        if (quote.ExpiresAt <= clock.GetUtcNow())
            throw PaymentsException.Unprocessable("quote_expired", "This quote has expired; request a new one.", "quote_id");

        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == from.UserId, ct);
        TransferRules.RequireCanTransact(user, "This user");
        var coinQuote = new CoinQuote(quote.From, quote.To, quote.Amount, quote.Fee, quote.AmountOut, quote.Rate);
        await quotes.RequireLiquidityAsync(from, coinQuote, ct);

        var fees = new List<FeeDto> { new("ndeipi", book.Format(quote.Fee, quote.From), quote.From) };
        var source = new TransferPartyDto("wallet", from.Id, from.UserId, from.Asset);
        var destination = new TransferPartyDto("wallet", to.Id, to.UserId, to.Asset);
        if (request.DryRun == true)
            return new TransferPreviewDto(TransferKind.Conversion, source, destination, book.Format(amount, from.Asset),
                new PreviewEstimate(fees, book.Format(quote.AmountOut, to.Asset), null, Rate: Rate(quote)));

        var buying = from.Asset == options.Value.Points.Asset;
        var points = options.Value.Points.Asset;
        var coin = options.Value.Coin.Asset;
        var treasuryPoints = await house.TreasuryAsync(points, ct);
        var inventory = await house.InventoryAsync(coin, ct);
        var feeAccount = await house.FeesAsync(from.Asset, ct);
        var fromAccount = await WalletAccountAsync(from.Id, ct);
        var toAccount = await WalletAccountAsync(to.Id, ct);

        var transfer = book.New(TransferKind.Conversion, "wallet", "wallet", from.Asset, amount, request, idempotencyKey);
        transfer.SourceWalletId = from.Id;
        transfer.SourceUserId = from.UserId;
        transfer.DestinationWalletId = to.Id;
        transfer.DestinationUserId = to.UserId;
        transfer.DestinationAsset = to.Asset;
        transfer.QuoteId = quote.Id;
        TransferBook.MoveUnsaved(transfer, TransferState.Completed);
        book.SetReceipt(transfer, new ReceiptDto(book.Format(amount, from.Asset), book.Format(quote.AmountOut, to.Asset), null, fees,
            transfer.CreatedAt, transfer.CreatedAt, Rate: Rate(quote)));

        // Into Ndeipi's stock or treasury goes the amount less the spread; out of the other comes what the quote promised.
        var lines = new List<LedgerLine> { new(fromAccount, -amount), new(buying ? treasuryPoints : inventory, amount - quote.Fee) };
        if (quote.Fee > 0)
            lines.Add(new(feeAccount, quote.Fee));
        lines.Add(new(buying ? inventory : treasuryPoints, -quote.AmountOut));
        lines.Add(new(toAccount, quote.AmountOut));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var claimed = await db.Quotes.Where(q => q.Id == quote.Id && q.Status == QuoteStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Status, QuoteStatus.Used).SetProperty(q => q.TransferId, transfer.Id), ct);
        if (claimed == 0)
            throw PaymentsException.Conflict("quote_used", "This quote has already settled a conversion.", field: "quote_id");
        db.Transfers.Add(transfer);
        await book.CreatedAsync(transfer, ct);
        await ledger.PostAsync(new PostingRequest($"Conversion {transfer.Id}", lines, transfer.Id, idempotencyKey), ct);
        await transaction.CommitAsync(ct);
        return book.To(transfer);
    }

    /// <summary>The account conversions use: a points wallet's cashable bucket, or a NdeipiCoin wallet's.</summary>
    Task<string> WalletAccountAsync(string walletId, CancellationToken ct) =>
        db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId == walletId && a.Bucket == LedgerBucket.Available).Select(a => a.Id).SingleAsync(ct);

    static string Rate(Quote q) => q.Rate.ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
}
