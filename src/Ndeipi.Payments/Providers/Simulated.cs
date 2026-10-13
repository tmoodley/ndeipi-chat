using System.Collections.Concurrent;

namespace Ndeipi.Payments.Providers;

/// <summary>
/// The sandbox's fiat rails (SRV-OPS-07, SC-06): the same rail codes as production, so integrators
/// test exactly what they will call, with no real money. Sandbox calls drive outcomes (FR-SBX-02).
/// </summary>
public sealed class SimulatedFiatRail(TimeProvider clock) : IFiatRail
{
    readonly ConcurrentDictionary<string, ProviderOperation> _operations = new();

    public string Name => "simulated_fiat";

    public IReadOnlyCollection<string> Rails { get; } = [RailCodes.AbsaEft, RailCodes.PayPal];

    public Task<FiatCollection> StartCollectionAsync(FiatCollectionRequest request, CancellationToken ct)
    {
        if (request.Amount is { } amount)
            _operations[request.Reference] = new(request.Reference, "sim_" + request.Reference, ProviderOperationStatus.Pending, amount, null, null, null, null, null);

        var expires = request.Amount is null ? (DateTimeOffset?)null : clock.GetUtcNow().AddDays(7);
        var code = request.Reference[^10..].ToUpperInvariant();
        return Task.FromResult(request.Rail == RailCodes.PayPal
            ? new FiatCollection("sim_" + request.Reference, "paypal", new Dictionary<string, string>
            {
                ["approval_url"] = $"https://paypal.sandbox.ndeipi.example/approve/{request.Reference}",
                ["reference"] = code
            }, expires)
            : new FiatCollection("sim_" + request.Reference, "bank", new Dictionary<string, string>
            {
                ["bank_name"] = "Ndeipi Sandbox Bank",
                ["account_name"] = "Ndeipi Sandbox",
                ["account_number"] = "000" + (request.Reference.Aggregate(17u, (hash, c) => hash * 31 + c) % 10_000_000).ToString("D7"),
                ["routing_code"] = "632005",
                ["reference"] = code
            }, expires));
    }

    public Task<ProviderOperation> SubmitPayoutAsync(FiatPayoutRequest request, CancellationToken ct) =>
        Task.FromResult(_operations.GetOrAdd(request.Reference, reference => new ProviderOperation(
            reference, "sim_" + reference, ProviderOperationStatus.Submitted, request.Amount, request.Amount, null, 0m, "SIM-" + reference[^8..].ToUpperInvariant(), null)));

    public Task<ProviderOperation?> GetStatusAsync(string reference, CancellationToken ct) =>
        Task.FromResult(_operations.TryGetValue(reference, out var operation) ? operation : null);

    public Task<ProviderStatement> GetStatementAsync(DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(new ProviderStatement(Name, new Dictionary<string, decimal>(), [], clock.GetUtcNow()));

    /// <summary>Sandbox: sets an operation's outcome, as the provider would report it.</summary>
    public ProviderOperation SetOutcome(string reference, ProviderOperationStatus status, string? reason = null) =>
        _operations.AddOrUpdate(reference,
            _ => throw new KeyNotFoundException(reference),
            (_, existing) => existing with { Status = status, Reason = reason });
}

/// <summary>The sandbox's exchange: converts instantly at 1:1 until real corridors and rates are known.</summary>
public sealed class SimulatedExchange(TimeProvider clock) : IExchangeProvider
{
    readonly ConcurrentDictionary<string, ProviderOperation> _operations = new();

    public string Name => "simulated_exchange";

    public Task<ProviderRate> GetRateAsync(string from, string to, CancellationToken ct) =>
        Task.FromResult(new ProviderRate(from, to, 1m, clock.GetUtcNow()));

    public Task<ProviderOperation> ConvertAsync(ConversionRequest request, CancellationToken ct) =>
        Task.FromResult(_operations.GetOrAdd(request.Reference, reference => new ProviderOperation(
            reference, "sim_" + reference, ProviderOperationStatus.Completed, request.Amount, request.Amount, 1m, 0m, null, null)));

    public Task<ProviderOperation?> GetStatusAsync(string reference, CancellationToken ct) =>
        Task.FromResult(_operations.TryGetValue(reference, out var operation) ? operation : null);

    public Task<ProviderStatement> GetStatementAsync(DateTimeOffset since, CancellationToken ct) =>
        Task.FromResult(new ProviderStatement(Name, new Dictionary<string, decimal>(), [], clock.GetUtcNow()));
}
