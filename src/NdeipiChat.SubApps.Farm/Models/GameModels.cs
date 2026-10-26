using System.Text.Json.Serialization;

namespace NdeipiChat.SubApps.Farm.Models;

public enum Species { Cattle, Goat }

public enum TileKind { Empty, Pasture, Kraal, CompostPit, UrineTank, RegenField, NodeBoss }

public enum VerificationStatus { Pending, Verified, Rejected }

public enum CropStage { Fallow, Planted, Growing, Ready }

public enum RegenPractice { NoTill, CoverCrop, Agroforestry, RotationalGrazing, CompostApplication, BioFertilizer }

public enum Weather { Sunny, Cloudy, Rainy }

/// <summary>A registered head of livestock (cattle or goat).</summary>
public class Animal
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
    public Species Species { get; set; }
    public string Name { get; set; } = "";
    public string TagNumber { get; set; } = "";
    public string Breed { get; set; } = "";
    public double WeightKg { get; set; }
    public int KraalIndex { get; set; }
    public int RegisteredDay { get; set; }
    public VerificationStatus Status { get; set; } = VerificationStatus.Pending;
    public string? Attestor { get; set; }
    public string? AttestationRef { get; set; }
    /// <summary>Cattle Bond reference — the instrument KandondoCoin wraps. Cattle only.</summary>
    public string? CattleBondRef { get; set; }
    public double Health { get; set; } = 100;

    [JsonIgnore] public string Icon => Species == Species.Cattle ? "🐄" : "🐐";
}

/// <summary>One plot on the farm grid.</summary>
public class Tile
{
    public int Index { get; set; }
    public TileKind Kind { get; set; }
    public int Level { get; set; } = 1;

    public double Forage { get; set; }     // pasture: standing forage · kraal: trough
    public double ManureKg { get; set; }   // kraal floor · compost pit input
    public double UrineL { get; set; }     // kraal sump · urine tank input
    public double CompostKg { get; set; }  // compost pit output
    public double BioFertL { get; set; }   // urine tank output

    public CropStage Crop { get; set; }
    public int CropDays { get; set; }

    /// <summary>Active practices. Value = days remaining, -1 = permanent.</summary>
    public Dictionary<RegenPractice, int> Practices { get; set; } = new();
    public double SoilCarbonT { get; set; }
}

public class Inventory
{
    public double ForageKg { get; set; }
    public double ManureKg { get; set; }
    public double UrineL { get; set; }
    public double CompostKg { get; set; }
    public double BioFertL { get; set; }
    public double ProduceKg { get; set; }
}

public class LedgerBlock
{
    public int Height { get; set; }
    public int Day { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public string PrevHash { get; set; } = "";
    public string Hash { get; set; } = "";
}

public class LogEntry
{
    public int Day { get; set; }
    public string Icon { get; set; } = "";
    public string Text { get; set; } = "";
}

public class FarmState
{
    public const int GridSize = 8;

    public int Version { get; set; } = 1;
    public string FarmName { get; set; } = "Namwala Plains Farm";
    public int Day { get; set; } = 1;
    public Weather Weather { get; set; } = Weather.Sunny;

    public double Ndx { get; set; } = 500;
    public double StakedNdx { get; set; }
    public double PendingCarbonT { get; set; }
    public double CarbonCredits { get; set; }
    public double LifetimeCreditsMinted { get; set; }
    public double CreditPrice { get; set; } = 12;
    public double SolarKwh { get; set; }

    public int Xp { get; set; }
    public double LifetimeManureKg { get; set; }
    public double LifetimeCompostKg { get; set; }

    public List<Tile> Tiles { get; set; } = new();
    public List<Animal> Animals { get; set; } = new();
    public Inventory Inventory { get; set; } = new();
    public List<LedgerBlock> Ledger { get; set; } = new();
    public List<LogEntry> Log { get; set; } = new();
    public HashSet<string> ClaimedMissions { get; set; } = new();

    [JsonIgnore] public int Level => 1 + Xp / 250;

    public static FarmState CreateNew()
    {
        var s = new FarmState();
        for (var i = 0; i < GridSize * GridSize; i++) s.Tiles.Add(new Tile { Index = i });

        // Starter homestead in the middle of the map
        s.Tiles[26].Kind = TileKind.Pasture;
        s.Tiles[26].Forage = 120;
        s.Tiles[27].Kind = TileKind.Kraal;
        s.Tiles[35].Kind = TileKind.RegenField;
        s.Inventory.ForageKg = 60;
        return s;
    }
}
