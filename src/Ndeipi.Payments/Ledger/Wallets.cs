using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Users;

namespace Ndeipi.Payments.Ledger;

public sealed record WalletCreateRequest(string? Asset, bool? AcknowledgePriceRisk, Dictionary<string, string>? Metadata);

/// <summary>The <c>WalletBalance</c> object (openapi.yaml).</summary>
public sealed record WalletBalanceDto(string WalletId, string Asset, string Available, string Pending, string Cashable, DateTimeOffset AsOf)
{
    public string Object => "wallet_balance";
}

/// <summary>The <c>Wallet</c> object (openapi.yaml).</summary>
public sealed record WalletDto(
    string Id,
    string UserId,
    string Asset,
    int Decimals,
    WalletStatus Status,
    WalletBalanceDto Balance,
    DateTimeOffset? PriceRiskAcknowledgedAt,
    IReadOnlyDictionary<string, string> Metadata,
    int Version,
    DateTimeOffset CreatedAt)
{
    public string Object => "wallet";
}

/// <summary>The <c>WalletHistoryEntry</c> object (openapi.yaml).</summary>
public sealed record WalletHistoryEntryDto(
    string Id,
    string WalletId,
    // Required by the contract even when null (a reward outside any transfer).
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TransferId,
    LedgerBucket Bucket,
    string Amount,
    string BalanceAfter,
    string Description,
    DateTimeOffset CreatedAt)
{
    public string Object => "wallet_history_entry";
}

/// <summary>
/// Wallets (SRS §4.2): one per approved user per asset, each a set of ledger accounts. Points
/// wallets have available (purchased, cashable), earned and pending buckets; NdeipiCoin wallets
/// have available and pending, and need the user's price-risk acknowledgement to open.
/// </summary>
public sealed class WalletService(PaymentsDbContext db, LedgerService ledger, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    PaymentsOptions Options => options.Value;

    /// <summary>The assets a wallet can hold, with their precision.</summary>
    (int Decimals, bool Earns, bool PriceRisk)? Rules(string asset) =>
        asset == Options.Points.Asset ? (Options.Points.Decimals, true, false)
        : asset == Options.Coin.Asset ? (Options.Coin.Decimals, false, true)
        : null;

    public async Task<WalletDto> CreateAsync(string userId, WalletCreateRequest request, CancellationToken ct)
    {
        var errors = new List<ApiErrorDetail>();
        if (string.IsNullOrEmpty(request.Asset))
            errors.Add(new("asset", "required", "asset is required."));
        else if (Rules(request.Asset) is null)
            errors.Add(new("asset", "unsupported_asset", $"Wallets hold {Options.Points.Asset} or {Options.Coin.Asset}."));
        UserService.ValidateMetadata(request.Metadata, errors);
        if (errors.Count > 0)
            throw PaymentsException.Validation(errors);
        var rules = Rules(request.Asset!)!.Value;

        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw PaymentsException.NotFound("user");
        TransferRules.RequireCanTransact(user, "This user");
        if (rules.PriceRisk && request.AcknowledgePriceRisk != true)
            throw PaymentsException.Unprocessable("price_risk_not_acknowledged",
                "NdeipiCoin's price moves. Show the user that risk and send acknowledge_price_risk: true.", "acknowledge_price_risk");
        if (await db.Wallets.AsNoTracking().Where(w => w.UserId == userId && w.Asset == request.Asset).Select(w => w.Id).FirstOrDefaultAsync(ct) is { } existing)
            throw PaymentsException.Conflict("wallet_exists", $"This user already has a {request.Asset} wallet.", existing, "asset");

        var now = clock.GetUtcNow();
        var wallet = new Wallet
        {
            Id = Ids.New(Ids.Wallet, clock),
            UserId = userId,
            Asset = request.Asset!,
            Decimals = rules.Decimals,
            Status = WalletStatus.Active,
            PriceRiskAcknowledgedAt = rules.PriceRisk ? now : null,
            MetadataJson = JsonSerializer.Serialize(request.Metadata ?? [], PaymentsJson.Options),
            CreatedAt = now
        };

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Wallets.Add(wallet);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.Wallets.AsNoTracking().Where(w => w.UserId == userId && w.Asset == request.Asset).Select(w => w.Id).FirstOrDefaultAsync(ct);
            throw winner is null ? new InvalidOperationException("Wallet could not be created.") :
                PaymentsException.Conflict("wallet_exists", $"This user already has a {request.Asset} wallet.", winner, "asset");
        }
        foreach (var bucket in rules.Earns ? [LedgerBucket.Available, LedgerBucket.Earned, LedgerBucket.Pending] : new[] { LedgerBucket.Available, LedgerBucket.Pending })
            await ledger.OpenAccountAsync(LedgerAccountKind.Wallet, bucket, wallet.Asset, wallet.Id, ct);
        await transaction.CommitAsync(ct);

        return To(wallet, Balance(wallet, []));
    }

    public async Task<WalletDto> GetAsync(string userId, string walletId, CancellationToken ct)
    {
        var wallet = await FindAsync(userId, walletId, ct);
        return To(wallet, Balance(wallet, await BucketsAsync([wallet.Id], ct)));
    }

    public async Task<Page<WalletDto>> ListAsync(string userId, string? asset, PageRequest pageRequest, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
            throw PaymentsException.NotFound("user");
        var query = db.Wallets.AsNoTracking().Where(w => w.UserId == userId);
        if (asset is not null)
            query = query.Where(w => w.Asset == asset);
        var page = await query.PageAsync(w => w.Id, pageRequest, w => w, ct);
        var buckets = await BucketsAsync([.. page.Data.Select(w => w.Id)], ct);
        return new Page<WalletDto>([.. page.Data.Select(w => To(w, Balance(w, buckets)))], page.HasMore);
    }

    public async Task<WalletBalanceDto> BalanceAsync(string userId, string walletId, CancellationToken ct)
    {
        var wallet = await FindAsync(userId, walletId, ct);
        return Balance(wallet, await BucketsAsync([wallet.Id], ct));
    }

    /// <summary>
    /// One entry per ledger line on the wallet's accounts, newest first. Summing an account's
    /// entries gives its balance (NFR-REL-03).
    /// </summary>
    public async Task<Page<WalletHistoryEntryDto>> HistoryAsync(
        string userId, string walletId, PageRequest page, DateTimeOffset? createdAfter, DateTimeOffset? createdBefore, CancellationToken ct)
    {
        var wallet = await FindAsync(userId, walletId, ct);
        var limit = page.Limit ?? PageRequest.DefaultLimit;
        if (limit is < 1 or > PageRequest.MaxLimit)
            throw PaymentsException.BadRequest($"limit must be between 1 and {PageRequest.MaxLimit}.", "limit");
        if (page.StartingAfter is not null && page.EndingBefore is not null)
            throw PaymentsException.BadRequest("Send starting_after or ending_before, not both.", "ending_before");
        long? Cursor(string? id, string field) => id is null ? null
            : Ids.ToNumber(Ids.WalletHistoryEntry, id) ?? throw PaymentsException.BadRequest($"{field} is not a wallet history entry ID.", field);
        var after = Cursor(page.StartingAfter, "starting_after");
        var before = Cursor(page.EndingBefore, "ending_before");

        var accounts = await db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId == wallet.Id).ToDictionaryAsync(a => a.Id, a => a.Bucket, ct);
        var ids = accounts.Keys.ToList();
        var lines = from line in db.PostingLines.AsNoTracking()
                    join posting in db.Postings.AsNoTracking() on line.PostingId equals posting.Id
                    where ids.Contains(line.AccountId)
                    select new { line.Id, line.AccountId, line.Amount, line.BalanceAfter, posting.TransferId, posting.Description, posting.CreatedAt };
        if (createdAfter is { } from)
            lines = lines.Where(l => l.CreatedAt > from);
        if (createdBefore is { } to)
            lines = lines.Where(l => l.CreatedAt < to);

        var rows = before is { } b
            ? [.. (await lines.Where(l => l.Id > b).OrderBy(l => l.Id).Take(limit + 1).ToListAsync(ct)).AsEnumerable().Reverse()]
            : await (after is { } a ? lines.Where(l => l.Id < a) : lines).OrderByDescending(l => l.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = rows.Count > limit;
        var shown = before is not null ? rows.Skip(Math.Max(0, rows.Count - limit)) : rows.Take(limit);

        return new Page<WalletHistoryEntryDto>([.. shown.Select(l => new WalletHistoryEntryDto(
            Ids.FromNumber(Ids.WalletHistoryEntry, l.Id), wallet.Id, l.TransferId, accounts[l.AccountId],
            Amounts.Format(l.Amount, wallet.Decimals), Amounts.Format(l.BalanceAfter, wallet.Decimals), l.Description, l.CreatedAt))], hasMore);
    }

    async Task<Wallet> FindAsync(string userId, string walletId, CancellationToken ct) =>
        await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.Id == walletId && w.UserId == userId, ct) ?? throw PaymentsException.NotFound("wallet");

    async Task<List<(string WalletId, LedgerBucket Bucket, decimal Balance)>> BucketsAsync(List<string> walletIds, CancellationToken ct) =>
        [.. (await db.LedgerAccounts.AsNoTracking().Where(a => a.WalletId != null && walletIds.Contains(a.WalletId))
            .Select(a => new { a.WalletId, a.Bucket, a.Balance }).ToListAsync(ct))
            .Select(a => (a.WalletId!, a.Bucket, a.Balance))];

    WalletBalanceDto Balance(Wallet wallet, List<(string WalletId, LedgerBucket Bucket, decimal Balance)> buckets)
    {
        decimal Sum(LedgerBucket bucket) => buckets.Where(b => b.WalletId == wallet.Id && b.Bucket == bucket).Sum(b => b.Balance);
        var available = Sum(LedgerBucket.Available);
        var cashable = wallet.Asset == Options.Points.Asset ? available : 0m;
        return new WalletBalanceDto(wallet.Id, wallet.Asset,
            Amounts.Format(available + Sum(LedgerBucket.Earned), wallet.Decimals),
            Amounts.Format(Sum(LedgerBucket.Pending), wallet.Decimals),
            Amounts.Format(cashable, wallet.Decimals),
            clock.GetUtcNow());
    }

    static WalletDto To(Wallet w, WalletBalanceDto balance) => new(
        w.Id, w.UserId, w.Asset, w.Decimals, w.Status, balance, w.PriceRiskAcknowledgedAt,
        JsonSerializer.Deserialize<Dictionary<string, string>>(w.MetadataJson, PaymentsJson.Options) ?? [], w.Version, w.CreatedAt);
}
