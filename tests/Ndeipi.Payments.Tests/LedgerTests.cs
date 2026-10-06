using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Ledger;
using Ndeipi.Payments.Tests.Infrastructure;

namespace Ndeipi.Payments.Tests;

/// <summary>The double-entry ledger (SRS §4.11, NFR-SRV-03).</summary>
public sealed class LedgerTests(PaymentsApp app) : IClassFixture<PaymentsApp>
{
    const string Usd = "usd-stable";

    /// <summary>A clearing account and two wallets, the first funded with <paramref name="funds"/>.</summary>
    async Task<(Guid Integrator, string Clearing, string Alice, string Bob)> SetUpAsync(decimal funds)
    {
        var integrator = await app.CreateIntegratorAsync();
        return await app.AsIntegratorAsync(integrator.Id, async sp =>
        {
            var ledger = sp.GetRequiredService<LedgerService>();
            var clearing = await ledger.OpenAccountAsync(LedgerAccountKind.ProviderClearing, LedgerBucket.Available, Usd, null, default, provider: "simulated_exchange");
            var alice = await ledger.OpenAccountAsync(LedgerAccountKind.Wallet, LedgerBucket.Available, Usd, Ids.New(Ids.Wallet, TimeProvider.System), default);
            var bob = await ledger.OpenAccountAsync(LedgerAccountKind.Wallet, LedgerBucket.Available, Usd, Ids.New(Ids.Wallet, TimeProvider.System), default);
            if (funds > 0)
                await ledger.PostAsync(new PostingRequest("Deposit", [new(clearing.Id, -funds), new(alice.Id, funds)]), default);
            return (integrator.Id, clearing.Id, alice.Id, bob.Id);
        });
    }

    Task<T> AsAsync<T>(Guid integrator, Func<LedgerService, Task<T>> work) =>
        app.AsIntegratorAsync(integrator, sp => work(sp.GetRequiredService<LedgerService>()));

    Task<decimal> BalanceAsync(string account) =>
        app.DbAsync(db => db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.Id == account).Select(a => a.Balance).SingleAsync());

    [Fact]
    public async Task A_posting_moves_both_balances_and_records_each_line_with_its_balance_after()
    {
        var (integrator, _, alice, bob) = await SetUpAsync(100m);

        var posting = await AsAsync(integrator, l => l.PostAsync(new PostingRequest("Alice pays Bob", [new(alice, -25m), new(bob, 25m)]), default));

        Assert.Equal(75m, await BalanceAsync(alice));
        Assert.Equal(25m, await BalanceAsync(bob));
        var lines = await app.DbAsync(db => db.PostingLines.Where(l => l.PostingId == posting.Id).ToListAsync());
        Assert.Equal(75m, lines.Single(l => l.AccountId == alice).BalanceAfter);
        Assert.Equal(25m, lines.Single(l => l.AccountId == bob).BalanceAfter);
    }

    [Fact]
    public async Task A_posting_that_does_not_balance_is_refused()
    {
        var (integrator, _, alice, bob) = await SetUpAsync(100m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AsAsync(integrator, l => l.PostAsync(new PostingRequest("Bad", [new(alice, -25m), new(bob, 20m)]), default)));
        Assert.Equal(100m, await BalanceAsync(alice));
    }

    [Fact]
    public async Task A_debit_beyond_the_available_balance_fails_and_moves_nothing()
    {
        var (integrator, _, alice, bob) = await SetUpAsync(10m);

        var thrown = await Assert.ThrowsAsync<PaymentsException>(() =>
            AsAsync(integrator, l => l.PostAsync(new PostingRequest("Too much", [new(alice, -10.01m), new(bob, 10.01m)]), default)));

        Assert.Equal("insufficient_funds", thrown.Error.Code);
        Assert.Equal(10m, await BalanceAsync(alice));
        Assert.Equal(0m, await BalanceAsync(bob));
    }

    [Fact]
    public async Task Concurrent_debits_never_take_a_wallet_below_zero_and_the_ledger_still_sums_to_zero()
    {
        var (integrator, _, alice, bob) = await SetUpAsync(50m);

        // 100 parallel debits of 1.00 against 50.00: exactly 50 succeed (SRV-LED-03, NFR-REL-02).
        var attempts = await Task.WhenAll(Enumerable.Range(0, 100).Select(async i =>
        {
            try
            {
                await AsAsync(integrator, l => l.PostAsync(new PostingRequest($"Debit {i}", [new(alice, -1m), new(bob, 1m)]), default));
                return true;
            }
            catch (PaymentsException e) when (e.Error.Code == "insufficient_funds")
            {
                return false;
            }
        }));

        Assert.Equal(50, attempts.Count(ok => ok));
        Assert.Equal(0m, await BalanceAsync(alice));
        Assert.Equal(50m, await BalanceAsync(bob));
        Assert.Equal(0m, await app.DbAsync(db => db.PostingLines.Where(l => l.Asset == Usd).SumAsync(l => l.Amount)));
        Assert.Equal(0m, await app.DbAsync(db => db.LedgerAccounts.IgnoreQueryFilters().Where(a => a.Asset == Usd).SumAsync(a => a.Balance)));
    }

    [Fact]
    public async Task The_same_idempotency_key_cannot_post_twice()
    {
        var (integrator, _, alice, bob) = await SetUpAsync(100m);
        var request = new PostingRequest("Once", [new(alice, -5m), new(bob, 5m)], IdempotencyKey: "key-1");

        await AsAsync(integrator, l => l.PostAsync(request, default));
        var thrown = await Assert.ThrowsAsync<PaymentsException>(() => AsAsync(integrator, l => l.PostAsync(request, default)));

        Assert.Equal(409, thrown.Status);
        Assert.Equal(95m, await BalanceAsync(alice));
    }

    [Fact]
    public async Task Postings_are_append_only_and_are_corrected_by_one_reversal()
    {
        var (integrator, _, alice, bob) = await SetUpAsync(100m);
        var posting = await AsAsync(integrator, l => l.PostAsync(new PostingRequest("Mistake", [new(alice, -30m), new(bob, 30m)]), default));

        await Assert.ThrowsAsync<InvalidOperationException>(() => app.AsIntegratorAsync(integrator, async sp =>
        {
            var db = sp.GetRequiredService<PaymentsDbContext>();
            var stored = await db.Postings.SingleAsync(p => p.Id == posting.Id);
            stored.Description = "Rewritten";
            return await db.SaveChangesAsync();
        }));

        var reversal = await AsAsync(integrator, l => l.ReverseAsync(posting.Id, "Correction", default));
        Assert.Equal(posting.Id, reversal.ReversesPostingId);
        Assert.Equal(100m, await BalanceAsync(alice));
        Assert.Equal(0m, await BalanceAsync(bob));

        await Assert.ThrowsAsync<InvalidOperationException>(() => AsAsync(integrator, l => l.ReverseAsync(posting.Id, "Again", default)));
        Assert.Equal(100m, await BalanceAsync(alice));
    }

    [Fact]
    public async Task One_integrator_cannot_post_to_another_integrators_accounts()
    {
        var (_, _, alice, _) = await SetUpAsync(100m);
        var (other, otherClearing, _, _) = await SetUpAsync(0m);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AsAsync(other, l => l.PostAsync(new PostingRequest("Theft", [new(alice, -10m), new(otherClearing, 10m)]), default)));
        Assert.Equal(100m, await BalanceAsync(alice));
    }
}
