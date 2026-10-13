using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Providers;

/// <summary>
/// The configured providers: which fiat rail serves each rail code, and the exchange. Routing by
/// rail code is what lets a second provider take a corridor without a contract change (SRV-PROV-09).
/// </summary>
public sealed class ProviderRegistry
{
    readonly Dictionary<string, IFiatRail> _byRail = new(StringComparer.Ordinal);

    public ProviderRegistry(IEnumerable<IFiatRail> rails, IExchangeProvider? exchange)
    {
        Rails = [.. rails];
        foreach (var rail in Rails)
            foreach (var code in rail.Rails)
                if (!_byRail.TryAdd(code, rail))
                    throw new InvalidOperationException($"Rail '{code}' is served by both {_byRail[code].Name} and {rail.Name}.");
        Exchange = exchange;
    }

    public IReadOnlyList<IFiatRail> Rails { get; }

    /// <summary>Null unless an exchange is configured; ramps never need one (Ndeipi Points are priced, not converted).</summary>
    public IExchangeProvider? Exchange { get; }

    public IReadOnlyCollection<string> RailCodes => _byRail.Keys;

    /// <summary>The provider for a rail code, or <c>422 unsupported_route</c>.</summary>
    public IFiatRail RailFor(string rail) =>
        _byRail.TryGetValue(rail, out var provider)
            ? provider
            : throw PaymentsException.Unprocessable("unsupported_route", $"No route uses the rail '{rail}'. See GET /v1/routes.", "rail");
}
