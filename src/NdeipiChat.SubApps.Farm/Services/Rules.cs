using NdeipiChat.SubApps.Farm.Models;

namespace NdeipiChat.SubApps.Farm.Services;

public record PracticeInfo(
    RegenPractice Practice,
    string Label,
    string Icon,
    TileKind AppliesTo,
    double NdxCost,
    double CompostKg,
    double BioFertL,
    int DurationDays,      // -1 = permanent
    double CarbonPerDay,   // game units, approx. tCO2e
    string Blurb);

/// <summary>
/// Game balance. Carbon factors are illustrative game values, NOT an MRV methodology.
/// Real issuance must follow a recognised standard (e.g. Verra VM0042 / Gold Standard).
/// </summary>
public static class Rules
{
    public const int MaxLevel = 3;

    public static readonly IReadOnlyDictionary<TileKind, double> BuildCost = new Dictionary<TileKind, double>
    {
        [TileKind.Empty] = 0,
        [TileKind.Pasture] = 20,
        [TileKind.Kraal] = 60,
        [TileKind.CompostPit] = 40,
        [TileKind.UrineTank] = 40,
        [TileKind.RegenField] = 30,
        [TileKind.NodeBoss] = 250,
    };

    public static double UpgradeCost(Tile t) => BuildCost[t.Kind] * t.Level * 1.5;

    // Pasture
    public const double PastureRegen = 45;
    public const double PastureCap = 200;

    // Manure & urine processing
    public const double CompostThroughput = 120;     // kg manure / day / level
    public const double CompostYield = 0.5;          // kg compost per kg manure
    public const double CompostCarbonPerKg = 0.0006; // avoided methane vs. open storage
    public const double UrineThroughput = 100;       // L / day / level
    public const double UrineCarbonPerL = 0.0004;    // displaced synthetic N

    // Crops
    public const int CropGrowDays = 4;
    public const double SeedCost = 5;
    public const double BaseYieldKg = 60;
    public const double ProducePrice = 0.6;          // NDX per kg

    // NodeBoss
    public const double NodeKwhPerDay = 40;
    public const double MinMintT = 0.25;

    public static int KraalCapacity(Tile k) => 6 * k.Level;
    public static double RegistrationFee(Species s) => s == Species.Cattle ? 10 : 4;
    public static double ForageNeed(Species s) => s == Species.Cattle ? 10 : 1.8;
    public static double ManurePerDay(Species s) => s == Species.Cattle ? 20 : 1.5;
    public static double UrinePerDay(Species s) => s == Species.Cattle ? 12 : 1.0;
    public static double DailyGainKg(Species s) => s == Species.Cattle ? 0.6 : 0.08;

    public static readonly IReadOnlyDictionary<RegenPractice, PracticeInfo> Practices = new Dictionary<RegenPractice, PracticeInfo>
    {
        [RegenPractice.NoTill] = new(RegenPractice.NoTill, "No-till", "🪱", TileKind.RegenField, 15, 0, 0, -1, 0.015,
            "Leave soil undisturbed to protect soil organic carbon."),
        [RegenPractice.CoverCrop] = new(RegenPractice.CoverCrop, "Cover crop", "☘️", TileKind.RegenField, 20, 0, 0, -1, 0.020,
            "Keep living roots in the ground between seasons."),
        [RegenPractice.Agroforestry] = new(RegenPractice.Agroforestry, "Agroforestry", "🌳", TileKind.RegenField, 60, 0, 0, -1, 0.035,
            "Plant trees along the field — long-term carbon storage."),
        [RegenPractice.RotationalGrazing] = new(RegenPractice.RotationalGrazing, "Rotational grazing", "🔄", TileKind.Pasture, 25, 0, 0, -1, 0.025,
            "Move the herd between paddocks; grass regrows 50% faster."),
        [RegenPractice.CompostApplication] = new(RegenPractice.CompostApplication, "Apply compost", "🍂", TileKind.RegenField, 0, 50, 0, 5, 0.030,
            "Spread manure compost. Crops grow a day faster for 5 days."),
        [RegenPractice.BioFertilizer] = new(RegenPractice.BioFertilizer, "Urine bio-fertilizer", "💧", TileKind.RegenField, 0, 0, 30, 5, 0.012,
            "Fermented cow urine replaces synthetic nitrogen for 5 days."),
    };

    public static string Label(TileKind k) => k switch
    {
        TileKind.Pasture => "Pasture",
        TileKind.Kraal => "Kraal",
        TileKind.CompostPit => "Compost pit",
        TileKind.UrineTank => "Urine bio-tank",
        TileKind.RegenField => "Regen field",
        TileKind.NodeBoss => "NodeBoss node",
        _ => "Empty plot",
    };

    public static string Icon(TileKind k) => k switch
    {
        TileKind.Pasture => "🌾",
        TileKind.Kraal => "🛖",
        TileKind.CompostPit => "🍂",
        TileKind.UrineTank => "🛢️",
        TileKind.RegenField => "🌱",
        TileKind.NodeBoss => "🔆",
        _ => "🟩",
    };

    public static string WeatherIcon(Weather w) => w switch
    {
        Weather.Sunny => "☀️",
        Weather.Cloudy => "⛅",
        _ => "🌧️",
    };

    public static double ForageFactor(Weather w) => w switch { Weather.Sunny => 0.9, Weather.Cloudy => 1.0, _ => 1.4 };
    public static double SolarFactor(Weather w) => w switch { Weather.Sunny => 1.0, Weather.Cloudy => 0.6, _ => 0.35 };
}
