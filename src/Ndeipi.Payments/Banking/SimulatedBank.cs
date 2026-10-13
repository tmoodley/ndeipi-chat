using System.Collections.Concurrent;
using Ndeipi.Payments.Data;

namespace Ndeipi.Payments.Banking;

/// <summary>
/// A business bank in memory, for the sandbox and tests (SC-06): two accounts, payments that
/// start processing, and cards. Like Meow it sends a payment once per idempotency key and hands out
/// a card number once. Production refuses to start on it.
/// </summary>
public sealed class SimulatedBank(TimeProvider clock) : IBusinessBank
{
    public const string CheckingId = "cash_account_sim_checking";
    public const string WalletId = "bridge_wallet_sim_usdc";

    readonly ConcurrentDictionary<string, BankAccountInfo> _accounts = new(
    [
        new(CheckingId, new BankAccountInfo(CheckingId, "Operating", "CHECKING", "5820", "USD", "OPEN", 250_000m, 250_000m, "Simulated Bank N.A.")),
        new(WalletId, new BankAccountInfo(WalletId, "USDC wallet", "DIGITALWALLET", "9f3a", "USD", "OPEN", 40_000m, 40_000m, null))
    ]);

    readonly ConcurrentDictionary<string, BankPaymentResult> _sent = new();
    readonly ConcurrentDictionary<string, (BankCard Card, bool Revealed)> _cards = new();
    readonly ConcurrentQueue<BankTransaction> _transactions = new();

    /// <summary>Each payment the bank accepted, by idempotency key: tests count these to prove it moved once.</summary>
    public IReadOnlyDictionary<string, BankPaymentResult> Sent => _sent;

    public int SendCalls;

    /// <summary>When set, the next sends fail as if the bank were down.</summary>
    public int FailNextSends;

    /// <summary>Amounts at or above this go to the bank's own approval, as Meow's security policy would.</summary>
    public decimal BankApprovalFrom { get; set; } = decimal.MaxValue;

    public Task<IReadOnlyList<BankAccountInfo>> ListAccountsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BankAccountInfo>>([.. _accounts.Values.OrderBy(a => a.Id)]);

    public Task<BankTransactionPage> ListTransactionsAsync(
        string accountId, DateTimeOffset? from, DateTimeOffset? to, string? cursor, int limit, CancellationToken ct)
    {
        if (!_accounts.ContainsKey(accountId))
            throw new BankRefusedException("Account not found.");
        var all = _transactions.Where(t => t.Id.StartsWith(accountId, StringComparison.Ordinal))
            .Where(t => (from is null || t.At >= from) && (to is null || t.At <= to))
            .OrderByDescending(t => t.At).ToList();
        var skip = cursor is null ? 0 : int.Parse(cursor);
        var page = all.Skip(skip).Take(limit).ToList();
        return Task.FromResult(new BankTransactionPage(page, skip + page.Count < all.Count ? (skip + page.Count).ToString() : null));
    }

    public Task<BankPayeeRef> CreatePayeeAsync(BankPayeeRequest request, CancellationToken ct)
    {
        if (request.Kind == PayeeKind.Ach && request.Bank is { RoutingNumber.Length: not 9 })
            throw new BankRefusedException("routing_number must be exactly 9 digits.");
        var contact = Guid.NewGuid().ToString();
        return Task.FromResult(new BankPayeeRef(contact, request.Kind == PayeeKind.International ? contact : Guid.NewGuid().ToString()));
    }

    public Task<BankPaymentResult> SendAsync(BankPaymentRequest r, CancellationToken ct)
    {
        Interlocked.Increment(ref SendCalls);
        if (FailNextSends > 0)
        {
            FailNextSends--;
            throw new BankUnavailableException("The simulated bank is down.");
        }
        var result = _sent.GetOrAdd(r.IdempotencyKey, _ =>
        {
            var account = _accounts.GetValueOrDefault(r.AccountId) ?? throw new BankRefusedException("Account not found.");
            if (account.Available < r.Amount)
                throw new BankRefusedException("Insufficient available balance.");
            _accounts[r.AccountId] = account with { Available = account.Available - r.Amount, Current = account.Current - r.Amount };
            _transactions.Enqueue(new BankTransaction(
                $"{r.AccountId}:{r.IdempotencyKey}", clock.GetUtcNow(), null, r.Purpose, r.PayeeId, r.Amount, "USD", false, "PENDING", "WITHDRAWAL"));
            var held = r.Amount >= BankApprovalFrom;
            return new BankPaymentResult(Guid.NewGuid().ToString(), held ? null : $"sim_{r.Rail.ToString().ToLowerInvariant()}_{Guid.NewGuid():N}",
                held ? "pending_approval" : "processing", held ? "Approve this payment in the bank's dashboard." : null);
        });
        return Task.FromResult(result);
    }

    /// <summary>Money arriving, as an inbound transfer would.</summary>
    public void Deposit(string accountId, decimal amount, string from)
    {
        var account = _accounts[accountId];
        _accounts[accountId] = account with { Available = account.Available + amount, Current = account.Current + amount };
        _transactions.Enqueue(new BankTransaction($"{accountId}:{Guid.NewGuid():N}", clock.GetUtcNow(), clock.GetUtcNow(), "Incoming transfer", from, amount, "USD", true, "POSTED", "DEPOSIT"));
    }

    public Task<BankCard> IssueCardAsync(BankCardRequest r, CancellationToken ct)
    {
        if (!_accounts.ContainsKey(r.DebitAccountId))
            throw new BankRefusedException("debit_account must be one of your open accounts.");
        var card = new BankCard(Guid.NewGuid().ToString(), r.Nickname, Random.Shared.Next(1000, 9999).ToString(), "active", "Ndeipi Treasury", r.DebitAccountId);
        _cards[card.Id] = (card, false);
        return Task.FromResult(card);
    }

    public Task<BankCard> UpdateCardAsync(string providerCardId, BankCardUpdate u, CancellationToken ct)
    {
        var (card, revealed) = _cards.GetValueOrDefault(providerCardId);
        if (card is null || card.Status == "closed")
            throw new BankRefusedException("Card not found.");
        card = card with
        {
            Nickname = u.Nickname ?? card.Nickname,
            Status = u.Frozen switch { true => "inactive", false => "active", null => card.Status }
        };
        _cards[providerCardId] = (card, revealed);
        return Task.FromResult(card);
    }

    public Task RevokeCardAsync(string providerCardId, CancellationToken ct)
    {
        var (card, revealed) = _cards.GetValueOrDefault(providerCardId);
        if (card is null)
            throw new BankRefusedException("Card not found.");
        _cards[providerCardId] = (card with { Status = "closed" }, revealed);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BankCard>> ListCardsAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<BankCard>>([.. _cards.Values.Select(c => c.Card)]);

    public Task<BankCardNumber> RevealCardNumberAsync(string providerCardId, CancellationToken ct)
    {
        var (card, revealed) = _cards.GetValueOrDefault(providerCardId);
        if (card is null)
            throw new BankRefusedException("Card not found.");
        if (revealed)
            throw new BankRefusedException("This card's number was already retrieved.");
        _cards[providerCardId] = (card, true);
        return Task.FromResult(new BankCardNumber("400000000000" + card.Last4, "123", 12, clock.GetUtcNow().Year + 3));
    }
}
