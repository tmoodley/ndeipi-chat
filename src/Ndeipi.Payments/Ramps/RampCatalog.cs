using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Providers;
using Ndeipi.Payments.Transfers;
using Ndeipi.Payments.Treasury;

namespace Ndeipi.Payments.Ramps;

public sealed record RouteEndpointDto(string Type, string? Asset, string? Currency, string? Rail, string? Country, int? Decimals);

/// <summary>The <c>Route</c> object (openapi.yaml).</summary>
public sealed record RouteDto(TransferKind Kind, RouteEndpointDto Source, RouteEndpointDto Destination, string MinAmount, string MaxAmount, int? SettlementTimeSeconds, bool Available);

/// <summary>The <c>Rate</c> object (openapi.yaml).</summary>
public sealed record RateDto(string From, string To, string Rate, string Type, DateTimeOffset AsOf, DateTimeOffset? ExpiresAt)
{
    public string Object => "rate";
}

/// <summary>
/// What can move where (FR-RATE-01): every rail the configured providers serve, in each currency
/// that both the rail carries and Ndeipi Points have a price in, plus user-to-user and conversions.
/// Rails are data here and nowhere else (DC-08).
/// </summary>
public sealed class RampCatalog(ProviderRegistry providers, PointsPricing pricing, IOptions<PaymentsOptions> options)
{
    /// <summary>Placeholders until Absa onboarding and PayPal approval fix the real terms.</summary>
    static readonly Dictionary<string, PaymentsOptions.RailOptions> Defaults = new()
    {
        [RailCodes.AbsaEft] = new() { Currencies = ["zar"], Country = "ZA", MinAmount = 10m, MaxAmount = 50_000m, SettlementSeconds = 86_400 },
        [RailCodes.PayPal] = new() { Currencies = ["usd"], MinAmount = 1m, MaxAmount = 10_000m, SettlementSeconds = 300, StandingDeposits = false }
    };

    PaymentsOptions Options => options.Value;

    /// <summary>The terms for one rail, or <c>422 unsupported_route</c> if no provider serves it.</summary>
    public PaymentsOptions.RailOptions Rail(string rail, string field = "rail")
    {
        if (!providers.RailCodes.Contains(rail))
            throw PaymentsException.Unprocessable("unsupported_route", $"No route uses the rail '{rail}'. See GET /v1/routes.", field);
        return (Options.Rails.Count > 0 ? Options.Rails : Defaults).GetValueOrDefault(rail)
            ?? throw PaymentsException.Unprocessable("unsupported_route", $"The rail '{rail}' has no terms configured.", field);
    }

    /// <summary>The rail's terms for a currency, which must also have a points price.</summary>
    public PaymentsOptions.RailOptions RailFor(string rail, string currency, string field = "currency")
    {
        var terms = Rail(rail);
        if (!terms.Currencies.Contains(currency) || !Options.Points.Prices.ContainsKey(currency))
            throw PaymentsException.Unprocessable("unsupported_route", $"The rail '{rail}' does not carry {currency}.", field);
        return terms;
    }

    /// <summary>Refuses an amount outside the rail's per-transfer limits.</summary>
    public static void RequireWithinLimits(PaymentsOptions.RailOptions terms, decimal fiat, string field = "amount")
    {
        if (fiat < terms.MinAmount)
            throw PaymentsException.Unprocessable("amount_below_minimum", $"The minimum on this route is {terms.MinAmount}.", field);
        if (fiat > terms.MaxAmount)
            throw PaymentsException.Unprocessable("amount_above_maximum", $"The maximum on this route is {terms.MaxAmount}.", field);
    }

    public IReadOnlyList<RouteDto> Routes(TransferKind? kind, string? country)
    {
        var points = Options.Points;
        var coin = Options.Coin;
        var pointsEnd = new RouteEndpointDto("wallet", points.Asset, null, null, null, points.Decimals);
        var coinEnd = new RouteEndpointDto("wallet", coin.Asset, null, null, null, coin.Decimals);
        var routes = new List<RouteDto>
        {
            new(TransferKind.UserToUser, pointsEnd, pointsEnd, Amounts.Format(0.01m, points.Decimals), Amounts.Format(1_000_000m, points.Decimals), 0, true),
            new(TransferKind.UserToUser, coinEnd, coinEnd, Amounts.Format(0.00000001m, coin.Decimals), Amounts.Format(1_000_000m, coin.Decimals), 0, true),
            new(TransferKind.Conversion, pointsEnd, coinEnd, Amounts.Format(0.01m, points.Decimals), Amounts.Format(1_000_000m, points.Decimals), 0, true),
            new(TransferKind.Conversion, coinEnd, pointsEnd, Amounts.Format(0.00000001m, coin.Decimals), Amounts.Format(1_000_000m, coin.Decimals), 0, true)
        };

        foreach (var code in providers.RailCodes.Order(StringComparer.Ordinal))
        {
            PaymentsOptions.RailOptions terms;
            try { terms = Rail(code); }
            catch (PaymentsException) { continue; }

            foreach (var currency in terms.Currencies.Where(points.Prices.ContainsKey))
            {
                var fiat = new RouteEndpointDto("fiat", null, currency, code, terms.Country, PointsPricing.FiatDecimals);
                var payout = fiat with { Type = "payout_account" };
                var min = Amounts.Format(terms.MinAmount, PointsPricing.FiatDecimals);
                var max = Amounts.Format(terms.MaxAmount, PointsPricing.FiatDecimals);
                routes.Add(new(TransferKind.Onramp, fiat, pointsEnd, min, max, terms.SettlementSeconds, true));
                // Off-ramp limits are in points, the unit the transfer is made in.
                routes.Add(new(TransferKind.Offramp, pointsEnd, payout,
                    Amounts.Format(pricing.PointsFor(currency, terms.MinAmount), points.Decimals),
                    Amounts.Format(pricing.PointsFor(currency, terms.MaxAmount), points.Decimals),
                    terms.SettlementSeconds, true));
            }
        }

        return [.. routes.Where(r => (kind is null || r.Kind == kind) &&
                                     (country is null || (r.Source.Country ?? r.Destination.Country) is null || (r.Source.Country ?? r.Destination.Country) == country))];
    }
}

/// <summary>
/// Rates (FR-RATE-02): points against fiat at the fixed price, and NdeipiCoin against points as a
/// guide from the last OTC trade (conversions settle at a locked quote instead).
/// </summary>
public sealed class RateService(PointsPricing pricing, CoinPricing coin, IOptions<PaymentsOptions> options, TimeProvider clock)
{
    public async Task<RateDto> GetAsync(string from, string to, CancellationToken ct)
    {
        var points = options.Value.Points.Asset;
        var coinAsset = options.Value.Coin.Asset;
        var now = clock.GetUtcNow();

        if (to == points && from != coinAsset)
            return new(from, to, Fixed(1 / pricing.PriceIn(from)), "fixed", now, null);
        if (from == points && to != coinAsset)
            return new(from, to, Fixed(pricing.PriceIn(to)), "fixed", now, null);

        if ((from, to) == (coinAsset, points) || (from, to) == (points, coinAsset))
        {
            var pointsPerCoin = await coin.PointsPerCoinAsync(ct);
            return new(from, to, Fixed(from == coinAsset ? pointsPerCoin : 1 / pointsPerCoin), "indicative", now, null);
        }
        throw PaymentsException.Unprocessable("unsupported_route", $"There is no rate from {from} to {to}.", "to");
    }

    static string Fixed(decimal rate) => decimal.Round(rate, 12, MidpointRounding.ToZero).ToString("0.############", System.Globalization.CultureInfo.InvariantCulture);
}
