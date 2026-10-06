using Microsoft.EntityFrameworkCore;
using Ndeipi.Payments.Data;
using Ndeipi.Payments.Providers;

namespace Ndeipi.Payments.Reconciliation;

public sealed record ReconciliationDifference(string Provider, string Asset, decimal Ledger, decimal Statement);

public sealed record ReconciliationReport(DateTimeOffset AsOf, IReadOnlyList<ReconciliationDifference> Differences)
{
    public bool Clean => Differences.Count == 0;
}

/// <summary>
/// Compares, for every provider, the float the ledger says Ndeipi holds there with what the
/// provider's own statement reports (SRV-PROV-06, NFR-SRV-03), and lists every difference. M4 adds
/// the daily schedule, per-movement matching and the operator alert; this is the balance check
/// they build on.
/// </summary>
public sealed class ReconciliationService(PaymentsDbContext db, ProviderRegistry providers, TimeProvider clock)
{
    public async Task<ReconciliationReport> RunAsync(DateTimeOffset since, CancellationToken ct)
    {
        var statements = new List<ProviderStatement>();
        foreach (var rail in providers.Rails)
            statements.Add(await rail.GetStatementAsync(since, ct));
        if (providers.Exchange is { } exchange)
            statements.Add(await exchange.GetStatementAsync(since, ct));

        // Clearing accounts mirror the provider's position, so the ledger's view is their balance negated.
        var ledger = await db.LedgerAccounts.IgnoreQueryFilters()
            .Where(a => a.Kind == LedgerAccountKind.ProviderClearing)
            .GroupBy(a => new { a.Provider, a.Asset })
            .Select(g => new { g.Key.Provider, g.Key.Asset, Balance = -g.Sum(a => a.Balance) })
            .ToListAsync(ct);

        var differences = new List<ReconciliationDifference>();
        foreach (var statement in statements)
        {
            var held = ledger.Where(l => l.Provider == statement.Provider).ToDictionary(l => l.Asset, l => l.Balance);
            foreach (var asset in held.Keys.Union(statement.Balances.Keys).Order(StringComparer.Ordinal))
            {
                var (mine, theirs) = (held.GetValueOrDefault(asset), statement.Balances.GetValueOrDefault(asset));
                if (mine != theirs)
                    differences.Add(new(statement.Provider, asset, mine, theirs));
            }
        }

        // A float in the ledger for a provider that is no longer configured is a difference too.
        var configured = statements.Select(s => s.Provider).ToHashSet();
        differences.AddRange(ledger.Where(l => !configured.Contains(l.Provider!) && l.Balance != 0)
            .Select(l => new ReconciliationDifference(l.Provider!, l.Asset, l.Balance, 0m)));

        return new ReconciliationReport(clock.GetUtcNow(), differences);
    }
}
