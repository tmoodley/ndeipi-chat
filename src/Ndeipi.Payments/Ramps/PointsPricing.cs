using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Ramps;

/// <summary>
/// The fixed price of Ndeipi Points in each currency (docs/payments-api/README.md, "Providers and
/// Ndeipi Points"). An on-ramp credits <see cref="PointsFor"/> and an off-ramp pays out
/// <see cref="FiatFor"/>.
///
/// Both round down, unlike other computed amounts (README decision 4): purchased points are a
/// liability the fiat Ndeipi holds must always cover, and rounding up on either side, even by a
/// fraction of a cent, could pay out more than came in once enough cash-outs add up.
/// </summary>
public sealed class PointsPricing(IOptions<PaymentsOptions> options)
{
    /// <summary>Fiat currencies are paid to two decimal places on both rails.</summary>
    public const int FiatDecimals = 2;

    PaymentsOptions.PointsOptions Points => options.Value.Points;

    public string Asset => Points.Asset;

    /// <summary>The price of one point in <paramref name="currency"/>, or <c>422 unsupported_route</c>.</summary>
    public decimal PriceIn(string currency) =>
        Points.Prices.TryGetValue(currency, out var price) && price > 0
            ? price
            : throw PaymentsException.Unprocessable("unsupported_route", $"Ndeipi Points are not sold in {currency}.", "currency");

    public decimal PointsFor(string currency, decimal fiat) =>
        decimal.Round(fiat / PriceIn(currency), Points.Decimals, MidpointRounding.ToZero);

    public decimal FiatFor(string currency, decimal points) =>
        decimal.Round(points * PriceIn(currency), FiatDecimals, MidpointRounding.ToZero);
}
