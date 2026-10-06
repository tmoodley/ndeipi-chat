using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;
using Ndeipi.Payments.Ramps;

namespace Ndeipi.Payments.Tests;

/// <summary>Ndeipi Points at a fixed price per currency: what an on-ramp credits and an off-ramp pays.</summary>
public sealed class PointsPricingTests
{
    static PointsPricing Pricing(params (string Currency, decimal Price)[] prices) =>
        new(Options.Create(new PaymentsOptions
        {
            Points = new() { Prices = prices.ToDictionary(p => p.Currency, p => p.Price) }
        }));

    [Fact]
    public void At_one_rand_a_point_rands_and_points_are_the_same_number()
    {
        var pricing = Pricing(("zar", 1.00m));

        Assert.Equal("ndeipi-points", pricing.Asset);
        Assert.Equal(250.00m, pricing.PointsFor("zar", 250.00m));
        Assert.Equal(99.99m, pricing.FiatFor("zar", 99.99m));
    }

    [Fact]
    public void Each_currency_has_its_own_price_and_fractions_round_down()
    {
        var pricing = Pricing(("zar", 1.00m), ("usd", 0.054m));

        Assert.Equal(0.054m, pricing.PriceIn("usd"));
        Assert.Equal(185.18m, pricing.PointsFor("usd", 10.00m)); // 185.185… points
        Assert.Equal(9.99m, pricing.FiatFor("usd", 185.18m));    // 9.99972 dollars
    }

    [Theory]
    [InlineData(0.054)]
    [InlineData(1.00)]
    [InlineData(18.37)]
    public void Cash_outs_never_add_up_to_more_fiat_than_came_in(decimal price)
    {
        var pricing = Pricing(("usd", price));

        foreach (var paid in new[] { 0.01m, 0.03m, 1.00m, 10.00m, 12.34m, 999.99m })
        {
            var points = pricing.PointsFor("usd", paid);
            Assert.True(pricing.FiatFor("usd", points) <= paid);

            // Cashed out in ten pieces rather than one.
            var piece = decimal.Round(points / 10, 2, MidpointRounding.ToZero);
            var pieces = Enumerable.Repeat(piece, 9).Append(points - piece * 9);
            Assert.True(pieces.Sum(p => pricing.FiatFor("usd", p)) <= paid);
        }
    }

    [Fact]
    public void A_currency_without_a_price_is_not_a_route()
    {
        var thrown = Assert.Throws<PaymentsException>(() => Pricing(("zar", 1.00m)).PointsFor("eur", 10m));

        Assert.Equal("unsupported_route", thrown.Error.Code);
        Assert.Equal("currency", thrown.Error.Field);
    }
}
