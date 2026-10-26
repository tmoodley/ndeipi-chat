using NdeipiChat.SubApps.Farm.Models;

namespace NdeipiChat.SubApps.Farm.Services;

public record Mission(string Id, string Title, string Description, double RewardNdx, int RewardXp, Func<FarmState, bool> IsComplete);

public static class MissionBook
{
    public static readonly IReadOnlyList<Mission> All = new List<Mission>
    {
        new("first-cow", "First of the herd", "Register your first head of cattle.", 25, 40,
            s => s.Animals.Any(a => a.Species == Species.Cattle)),
        new("goats", "Goat keeper", "Register 3 goats.", 20, 30,
            s => s.Animals.Count(a => a.Species == Species.Goat) >= 3),
        new("manure", "Black gold", "Collect 200 kg of manure from your kraals.", 30, 40,
            s => s.LifetimeManureKg >= 200),
        new("compost", "Close the loop", "Produce 100 kg of compost.", 30, 50,
            s => s.LifetimeCompostKg >= 100),
        new("regen", "Regenerator", "Run 3 regenerative practices at once.", 40, 60,
            s => s.Tiles.Sum(t => t.Practices.Count) >= 3),
        new("node", "Plug into NodeBoss", "Build a solar NodeBoss node.", 50, 60,
            s => s.Tiles.Any(t => t.Kind == TileKind.NodeBoss)),
        new("mint", "First credit", "Verify and mint your first carbon credit.", 60, 80,
            s => s.LifetimeCreditsMinted > 0),
        new("verified-5", "Bankable herd", "Get 5 animals verified on the ledger.", 75, 100,
            s => s.Animals.Count(a => a.Status == VerificationStatus.Verified) >= 5),
        new("stake", "Node operator", "Stake 100 NDX into NodeBoss.", 40, 50,
            s => s.StakedNdx >= 100),
    };
}
