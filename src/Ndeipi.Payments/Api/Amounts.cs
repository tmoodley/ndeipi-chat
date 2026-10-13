using System.Globalization;

namespace Ndeipi.Payments.Api;

/// <summary>
/// Amounts on the wire: decimal strings at exactly their asset's precision (README decision 4).
/// The ledger stores 18 places; responses show the asset's own, so "25.00" stays "25.00".
/// </summary>
public static class Amounts
{
    public static string Format(decimal value, int decimals) =>
        decimal.Round(value, decimals, MidpointRounding.ToZero).ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>A positive amount with no more decimal places than the asset allows; never rounded.</summary>
    public static void RequireValid(decimal amount, int decimals, string asset, string field = "amount")
    {
        if (amount <= 0)
            throw PaymentsException.Unprocessable("amount_below_minimum", "The amount must be more than zero.", field);
        if (decimal.Round(amount, decimals) != amount)
            throw PaymentsException.Unprocessable("invalid_amount_precision", $"{asset} amounts carry at most {decimals} decimal places.", field);
    }
}
