using System.Globalization;

namespace NdeipiChat.Contracts;

/// <summary>
/// Ndeipi RWA (RWA &amp; Time-Stripping Micro-App SRS 1.0), phase 1: the Green Island (Negril, Jamaica)
/// villa pre-sale. People who have passed KYC buy a week (1/52, priced by season), a month (1/12) or a
/// whole year (1/1) of a villa; the money sits in a cooling-off escrow they can cancel from for a full
/// refund (FR-1.3), then goes into the villa's Year 1 vault at its current NAV (FR-2.2), whose value
/// rises when the oracle posts a higher NAV (FR-2.4).
///
/// Everything is simulated until a legal review passes: purchases settle against a test balance in US
/// dollars, no Ndeipi Points or other money moves, and nothing is minted on Polygon.
/// </summary>
public static class RwaContract
{
    public const string AppId = "rwa";

    /// <summary>The SRS's own path (section 5).</summary>
    public const string BasePath = "api/v1/rwa";

    /// <summary>Shown on every screen while settlement is simulated.</summary>
    public const string SimulatedNotice = "Simulated preview. Not an offer of securities or timeshare. No real money moves and nothing is minted.";

    public const int WeeksPerYear = 52, MonthsPerYear = 12;

    public static string Money(decimal amount) => "$" + amount.ToString("#,0.00", CultureInfo.InvariantCulture);

    /// <summary>A share fraction as the SRS writes it: 1/52, 1/12 or 1/1.</summary>
    public static string Fraction(string intervalType) => intervalType switch
    {
        RwaIntervals.Week => "1/52",
        RwaIntervals.Month => "1/12",
        _ => "1/1"
    };

    public static decimal ShareFraction(string intervalType) => intervalType switch
    {
        RwaIntervals.Week => 1m / WeeksPerYear,
        RwaIntervals.Month => 1m / MonthsPerYear,
        _ => 1m
    };

    /// <summary>"Week 14", "Month 3 (weeks 9-13)" or "Full year".</summary>
    public static string IntervalLabel(string intervalType, int index) => intervalType switch
    {
        RwaIntervals.Week => $"Week {index}",
        RwaIntervals.Month => $"Month {index} (weeks {RwaCalendar.MonthWeeks(index).First}-{RwaCalendar.MonthWeeks(index).Last})",
        _ => "Full year"
    };
}

/// <summary>The SRS's interval types (villa_epoch_rights.interval_type).</summary>
public static class RwaIntervals
{
    public const string Week = "WEEK", Month = "MONTH", FullYear = "FULL_YEAR";

    public static bool IsValid(string? type) => type is Week or Month or FullYear;
}

/// <summary>rwa_assets.asset_class. Only villas are on sale in phase 1.</summary>
public static class RwaAssetClasses
{
    public const string ReitVilla = "REIT_VILLA", CattleBond = "CATTLE_BOND", MiningRwa = "MINING_RWA", GoldCoupon = "GOLD_COUPON";
}

/// <summary>rwa_assets.construction_phase.</summary>
public static class RwaPhases
{
    public const string LandPresale = "LAND_PRESALE", Y1YieldLock = "Y1_YIELD_LOCK", Building = "BUILDING", Operating = "OPERATING";

    public static string Label(string phase) => phase switch
    {
        LandPresale => "Land pre-sale",
        Y1YieldLock => "Year 1 yield lock",
        Building => "Building",
        Operating => "Operating",
        _ => phase
    };
}

/// <summary>A holding's life: cooling off (cancellable), then vested in the Year 1 vault, or cancelled and refunded.</summary>
public static class RwaHoldingStatus
{
    public const string CoolingOff = "cooling_off", Vested = "vested", Cancelled = "cancelled";
}

/// <summary>How a purchase is paid. Only <see cref="Simulated"/> settles in phase 1; the others are refused until their gate opens.</summary>
public static class RwaPaymentMethods
{
    public const string Simulated = "simulated", Points = "points", Absa = "absa", PayPal = "paypal";
}

/// <summary>The settlement gate (configuration "Rwa:Mode").</summary>
public static class RwaModes
{
    /// <summary>Nothing can be bought; the catalogue and portfolio still show.</summary>
    public const string Off = "Off";

    /// <summary>Purchases settle against a test balance. No real money, no mint.</summary>
    public const string Simulated = "Simulated";
}

/// <summary>Negril's seasons, by week of the year, and the ADR multiplier each one weights a weekly tranche by (FR-2.1).</summary>
public static class RwaSeasons
{
    public const string Peak = "PEAK", Shoulder = "SHOULDER", OffPeak = "OFF_PEAK";

    /// <summary>Peak: mid-December to mid-April. Shoulder: mid-April to mid-June, and November to mid-December. Off-peak: the hurricane months.</summary>
    public static string Of(int week) => week switch
    {
        <= 15 or >= 51 => Peak,
        <= 24 or >= 45 => Shoulder,
        _ => OffPeak
    };

    public static decimal Weight(string season) => season switch
    {
        Peak => 1.40m,
        Shoulder => 1.00m,
        _ => 0.70m
    };

    public static string Label(string season) => season switch
    {
        Peak => "Peak",
        Shoulder => "Shoulder",
        _ => "Off-peak"
    };

    /// <summary>The 52 weeks' weights added up, so the 52 weekly prices add up to the full-year price.</summary>
    public static readonly decimal TotalWeight = Enumerable.Range(1, RwaContract.WeeksPerYear).Sum(w => Weight(Of(w)));
}

/// <summary>Which weeks each interval takes on the villa's calendar.</summary>
public static class RwaCalendar
{
    /// <summary>A 4-4-5 calendar: each quarter is 13 weeks, months of 4, 4 and 5 weeks.</summary>
    public static (int First, int Last) MonthWeeks(int month)
    {
        var quarter = (month - 1) / 3;
        var position = (month - 1) % 3;
        var first = quarter * 13 + position * 4 + 1;
        return (first, first + (position == 2 ? 4 : 3));
    }

    public static IEnumerable<int> Weeks(string intervalType, int index) => intervalType switch
    {
        RwaIntervals.Week => [index],
        RwaIntervals.Month => Enumerable.Range(MonthWeeks(index).First, MonthWeeks(index).Last - MonthWeeks(index).First + 1),
        _ => Enumerable.Range(1, RwaContract.WeeksPerYear)
    };

    public static bool IsValidIndex(string intervalType, int index) => intervalType switch
    {
        RwaIntervals.Week => index is >= 1 and <= RwaContract.WeeksPerYear,
        RwaIntervals.Month => index is >= 1 and <= RwaContract.MonthsPerYear,
        RwaIntervals.FullYear => index == 1,
        _ => false
    };
}

/// <summary>Tranche prices (FR-2.1), shared by the app and the server so both show the same number.</summary>
public static class RwaPricing
{
    /// <summary>
    /// A week is the unit price weighted by its season's ADR multiplier over the year's total weight;
    /// a month is 1/12 of the unit price; a full year is the unit price.
    /// </summary>
    public static decimal Price(decimal unitPriceUsd, string intervalType, int index) => intervalType switch
    {
        RwaIntervals.Week => Math.Round(unitPriceUsd * RwaSeasons.Weight(RwaSeasons.Of(index)) / RwaSeasons.TotalWeight, 2),
        RwaIntervals.Month => Math.Round(unitPriceUsd / RwaContract.MonthsPerYear, 2),
        _ => Math.Round(unitPriceUsd, 2)
    };
}

public sealed record RwaMeDto(
    string Mode,
    bool KycVerified,
    bool IsOracle,
    decimal SimBalanceUsd,
    int CoolingOffDays,
    string Notice);

/// <param name="AssetsPerShare">The Year 1 vault's NAV per share (ERC-4626 convertToAssets(1)): current NAV over base NAV.</param>
/// <param name="WeeksSold">Weeks of the 52 already taken by a holding in cooling-off or vested.</param>
public sealed record RwaAssetDto(
    Guid Id,
    string Code,
    string Name,
    string Location,
    string AssetClass,
    string TokenStandard,
    string Summary,
    int Bedrooms,
    decimal UnitPriceUsd,
    decimal BaseNavUsd,
    decimal CurrentNavUsd,
    decimal AssetsPerShare,
    string ConstructionPhase,
    int WeeksSold,
    DateTimeOffset NavUpdatedAt);

/// <param name="Taken">Already sold (or part of a month or year that is).</param>
public sealed record RwaWeekDto(int Week, string Season, decimal SeasonWeight, decimal PriceUsd, bool Taken);

public sealed record RwaMonthDto(int Month, int FirstWeek, int LastWeek, decimal PriceUsd, bool Available);

public sealed record RwaAssetDetailDto(
    RwaAssetDto Asset,
    IReadOnlyList<string> Highlights,
    IReadOnlyList<RwaWeekDto> Weeks,
    IReadOnlyList<RwaMonthDto> Months,
    decimal FullYearPriceUsd,
    bool FullYearAvailable);

/// <summary>FR-2.1 (POST presale/mint). The buyer is whoever is signed in, never a user id in the body.</summary>
/// <param name="RequestId">Picked by the app once per purchase, so a retried request can't buy twice.</param>
public sealed record RwaMintRequest(string AssetCode, string IntervalType, int IntervalIndex, string PaymentMethod, Guid RequestId);

/// <param name="TokenId">The base villa token's number (ERC-1155 id mirror). No token exists on chain while simulated.</param>
/// <param name="VaultShares">Shares of the Year 1 vault, set when the cooling-off ends.</param>
/// <param name="ValueUsd">What it's worth now: the principal while cooling off, the vault shares at today's NAV after.</param>
public sealed record RwaHoldingDto(
    Guid Id,
    long TokenId,
    string AssetCode,
    string AssetName,
    string IntervalType,
    int IntervalIndex,
    string IntervalLabel,
    string Season,
    string Fraction,
    decimal PrincipalUsd,
    string PaymentMethod,
    string Status,
    DateTimeOffset PurchasedAt,
    DateTimeOffset CoolingOffEndsAt,
    DateTimeOffset? VestedAt,
    DateTimeOffset? CancelledAt,
    decimal VaultShares,
    decimal ValueUsd,
    bool Simulated);

/// <summary>GET portfolio (SRS section 5), phase 1 part: villa holdings, their NAV, and the test balance.</summary>
public sealed record RwaPortfolioDto(
    IReadOnlyList<RwaHoldingDto> Holdings,
    decimal InvestedUsd,
    decimal ValueUsd,
    decimal InCoolingOffUsd,
    decimal SimBalanceUsd);

/// <summary>FR-2.4: an oracle posts a villa's new NAV, with a reference to what it was based on (an audit, a valuation).</summary>
public sealed record RwaNavUpdateRequest(string AssetCode, decimal NewNavUsd, string Attestation);
