using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Ledger;

/// <summary>One side of a posting: credit an account with a positive amount, debit it with a negative one.</summary>
public sealed record LedgerLine(string AccountId, decimal Amount);

public sealed record PostingRequest(
    string Description,
    IReadOnlyList<LedgerLine> Lines,
    string? TransferId = null,
    string? IdempotencyKey = null,
    string? ReversesPostingId = null);

/// <summary>
/// The double-entry ledger (SRS §4.11) and the only writer of balances (SC-02).
///
/// A posting's lines must sum to zero per asset (SRV-LED-01). Each line moves its account's balance
/// with one conditional UPDATE, so concurrent debits on one wallet serialise on the row lock and a
/// debit that would take a wallet below zero matches no row and fails the whole posting
/// (SRV-LED-03). Accounts are locked in ID order so two postings never deadlock on each other.
/// A database check constraint backs the rule up.
///
/// Callers that write other rows with the posting (a transfer, its event) open the transaction
/// themselves; the posting joins it and commits with them (SRV-LED-04, SC-04).
/// </summary>
public sealed class LedgerService(PaymentsDbContext db, IntegratorScope scope, IHttpContextAccessor http, TimeProvider clock)
{
    Guid Integrator => scope.IntegratorId ?? throw new InvalidOperationException("Ledger work needs an integrator scope.");

    public async Task<LedgerAccount> OpenAccountAsync(
        LedgerAccountKind kind, LedgerBucket bucket, string asset, string? walletId, CancellationToken ct, string? provider = null)
    {
        if ((kind == LedgerAccountKind.ProviderClearing) != (provider is not null))
            throw new InvalidOperationException("Provider clearing accounts, and only they, name their provider.");
        var account = new LedgerAccount
        {
            Id = Ids.New(Ids.Account, clock),
            IntegratorId = Integrator,
            Kind = kind,
            Bucket = bucket,
            WalletId = walletId,
            Provider = provider,
            Asset = asset,
            AllowNegative = kind is not LedgerAccountKind.Wallet,
            CreatedAt = clock.GetUtcNow()
        };
        db.LedgerAccounts.Add(account);
        await db.SaveChangesAsync(ct);
        return account;
    }

    public async Task<Posting> PostAsync(PostingRequest request, CancellationToken ct)
    {
        Validate(request);
        var ids = request.Lines.Select(l => l.AccountId).ToHashSet();
        var accounts = await db.LedgerAccounts.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        if (request.Lines.FirstOrDefault(l => !accounts.ContainsKey(l.AccountId)) is { } missing)
            throw new InvalidOperationException($"Ledger account {missing.AccountId} does not exist for this integrator.");
        RequireBalanced(request.Lines.Select(l => (accounts[l.AccountId].Asset, l.Amount)));

        var owned = db.Database.CurrentTransaction is null;
        await using var transaction = owned ? await db.Database.BeginTransactionAsync(ct) : null;

        var posting = new Posting
        {
            Id = Ids.New(Ids.Posting, clock),
            IntegratorId = Integrator,
            TransferId = request.TransferId,
            Description = request.Description,
            IdempotencyKey = request.IdempotencyKey,
            ReversesPostingId = request.ReversesPostingId,
            RequestId = http.HttpContext is { } context ? RequestIds.Get(context) : null,
            ApiKeyId = scope.ApiKeyId,
            CreatedAt = clock.GetUtcNow()
        };

        foreach (var line in request.Lines.OrderBy(l => l.AccountId, StringComparer.Ordinal))
        {
            var balance = await MoveAsync(line, ct)
                ?? throw PaymentsException.Unprocessable("insufficient_funds", "The available balance is too low for this transfer.", "amount");
            posting.Lines.Add(new PostingLine
            {
                PostingId = posting.Id,
                AccountId = line.AccountId,
                Asset = accounts[line.AccountId].Asset,
                Amount = line.Amount,
                BalanceAfter = balance
            });
        }

        db.Postings.Add(posting);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The balance moves roll back with the transaction; don't leave the posting to be saved later.
            foreach (var line in posting.Lines.ToList())
                db.Entry(line).State = EntityState.Detached;
            db.Entry(posting).State = EntityState.Detached;
            if (request.IdempotencyKey is not null && request.ReversesPostingId is null)
                throw PaymentsException.Conflict("idempotency_key_reused", "Funds have already moved for this Idempotency-Key.");
            throw;
        }
        if (transaction is not null)
            await transaction.CommitAsync(ct);
        return posting;
    }

    /// <summary>Corrects a posting with a new one that mirrors it (SRV-LED-02). A posting is reversed at most once.</summary>
    public async Task<Posting> ReverseAsync(string postingId, string description, CancellationToken ct)
    {
        var original = await db.Postings.AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == postingId, ct)
            ?? throw new InvalidOperationException($"Posting {postingId} does not exist for this integrator.");
        if (await db.Postings.AnyAsync(p => p.ReversesPostingId == postingId, ct))
            throw AlreadyReversed(postingId);

        try
        {
            return await PostAsync(new PostingRequest(
                description,
                [.. original.Lines.Select(l => new LedgerLine(l.AccountId, -l.Amount))],
                original.TransferId,
                ReversesPostingId: postingId), ct);
        }
        catch (DbUpdateException)
        {
            // Reversed by another request between the check above and this one.
            throw AlreadyReversed(postingId);
        }
    }

    static InvalidOperationException AlreadyReversed(string postingId) => new($"Posting {postingId} has already been reversed.");

    /// <summary>The new balance, or null when the account may not go negative and the debit would take it there.</summary>
    async Task<decimal?> MoveAsync(LedgerLine line, CancellationToken ct)
    {
        var integrator = Integrator;
        var rows = await db.Database.SqlQuery<decimal>($"""
            UPDATE [payments].[LedgerAccounts]
            SET [Balance] = [Balance] + {line.Amount}
            OUTPUT inserted.[Balance] AS [Value]
            WHERE [Id] = {line.AccountId} AND [IntegratorId] = {integrator}
              AND ([AllowNegative] = 1 OR [Balance] + {line.Amount} >= 0)
            """).ToListAsync(ct);
        return rows.Count == 1 ? rows[0] : null;
    }

    static void Validate(PostingRequest request)
    {
        if (request.Lines.Count < 2)
            throw new InvalidOperationException("A posting needs at least two lines.");
        if (request.Lines.Any(l => l.Amount == 0))
            throw new InvalidOperationException("A posting line cannot be zero.");
        if (request.Lines.Select(l => l.AccountId).Distinct().Count() != request.Lines.Count)
            throw new InvalidOperationException("A posting touches each account at most once.");
    }

    /// <summary>Debits equal credits in every asset (SRV-LED-01).</summary>
    public static void RequireBalanced(IEnumerable<(string Asset, decimal Amount)> lines)
    {
        foreach (var asset in lines.GroupBy(l => l.Asset))
            if (asset.Sum(l => l.Amount) != 0)
                throw new InvalidOperationException($"Posting does not balance in {asset.Key}.");
    }
}
