namespace NdeipiChat.Contracts;

/// <summary>
/// CyberWars: GN Mining, a Command &amp; Conquer-style RTS about Zambian artisanal mining (SRS v1.1).
/// The server runs the only simulation (NFR-2): clients POST orders and draw the state deltas the
/// match pushes on the player's topic every 100 ms tick. Combat only ever moves play credits; real
/// assets (NDX, Coop Coin) never enter a match (section 8).
/// </summary>
public static class CyberWarsContract
{
    public const string AppId = "cyberwars";
    public const string BasePath = "api/cyberwars";

    /// <summary>One authoritative step (NFR-1).</summary>
    public const int TickMilliseconds = 100;

    public const int TicksPerSecond = 1000 / TickMilliseconds;

    /// <summary>The most orders one batch may carry.</summary>
    public const int MaxOrdersPerBatch = 20;

    /// <summary>One player's view of one match: only what their units can see (NFR-2b).</summary>
    public static string PlayerTopic(Guid matchId, Guid userId) => $"cyberwars:{matchId:N}:{userId:N}";

    /// <summary>Map-wide changes for everyone: sector owners, pings, match end (Phase 02).</summary>
    public static string MapTopic(ZambianProvince province) => $"cyberwars:map:{province}";
}

public enum PlayerRank { None = 0, Explorer = 1, Commander = 2, Sovereign = 3 }

public enum ZambianProvince
{
    Central, Copperbelt, Eastern, Luapula, Lusaka, Muchinga, Northern, NorthWestern, Southern, Western
}

public enum RigRarity { Common, Rare, Epic, Legendary }

/// <summary>
/// The spec's order kinds, plus <see cref="Place"/> (put a finished structure on the map) and
/// <see cref="Cancel"/> (take an item off the build queue, FR-1.2a).
/// </summary>
public enum OrderKind { Build, Move, AttackMove, Attack, Capture, Stop, Harvest, UseStratagem, Place, Cancel }

/// <summary>A hex cell in axial coordinates.</summary>
public readonly record struct HexCell(int Q, int R)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public int S => -Q - R;

    public static int Distance(HexCell a, HexCell b) => (Math.Abs(a.Q - b.Q) + Math.Abs(a.R - b.R) + Math.Abs(a.S - b.S)) / 2;

    static readonly (int Q, int R)[] Directions = [(1, 0), (1, -1), (0, -1), (-1, 0), (-1, 1), (0, 1)];

    public IEnumerable<HexCell> Neighbours()
    {
        foreach (var (q, r) in Directions)
            yield return new HexCell(Q + q, R + r);
    }

    /// <summary>Every cell within <paramref name="radius"/> of this one, this one included.</summary>
    public IEnumerable<HexCell> Within(int radius)
    {
        for (var q = -radius; q <= radius; q++)
            for (var r = Math.Max(-radius, -q - radius); r <= Math.Min(radius, -q + radius); r++)
                yield return new HexCell(Q + q, R + r);
    }
}

/// <param name="OrderId">Client-made, so a retried order is applied once.</param>
/// <param name="ClientTick">The tick the client saw when the player gave the order.</param>
/// <param name="ItemKey">What to build, place or cancel, or which stratagem, e.g. "rig.rare".</param>
public sealed record RtsOrder(
    Guid OrderId,
    long ClientTick,
    OrderKind Kind,
    IReadOnlyList<Guid> UnitIds,
    HexCell? Target,
    Guid? TargetEntityId,
    string? ItemKey);

public sealed record OrderBatchRequest(IReadOnlyList<RtsOrder> Orders);

/// <param name="Tick">The tick the order applies at, or the tick it was refused on.</param>
public sealed record OrderResultDto(Guid OrderId, bool Accepted, long Tick, string? Reason);

/// <param name="Kind">An item key from <see cref="CyberWarsRules"/> ("harvester", "obw"), or "vein.gold" / "vein.copper".</param>
/// <param name="Health">Hit points; for a vein, the ore left in it.</param>
/// <param name="State">What it's doing ("moving", "loading 40/100", "stock 120"); null when idle.</param>
public sealed record EntityDto(Guid Id, Guid OwnerId, string Kind, HexCell At, int Health, string? State);

/// <param name="Started">False while waiting its turn or for credits; its cost is charged when it starts (FR-1.2a).</param>
/// <param name="Ready">A structure that is built and waiting to be placed.</param>
public sealed record BuildJobDto(Guid JobId, string ItemKey, string Factory, double Progress, bool Started, bool Ready);

public static class MatchStatuses
{
    public const string Running = "running";
    public const string Won = "won";
    public const string Ended = "ended";
}

/// <summary>What changed for one player in one tick.</summary>
/// <param name="WinnerId">Who won, once the match is over and someone did.</param>
/// <param name="GnTokensEarned">GN Tokens picked up from scrap crates this match; added to the account when it ends.</param>
/// <param name="UnderAi">Players away long enough that the AI is defending for them (FR-2.4).</param>
/// <param name="OreRefined">Every ore credit the player has refined this match; the skirmish is won at <see cref="CyberWarsRules.SkirmishGoal"/>.</param>
/// <param name="Queue">The player's whole build queue, every tick (it is small).</param>
public sealed record MatchDeltaDto(
    Guid MatchId,
    long Tick,
    IReadOnlyList<EntityDto> Changed,
    IReadOnlyList<Guid> Removed,
    decimal OreCredits,
    int PowerSupply,
    int PowerDemand,
    IReadOnlyList<string> Cues,
    decimal OreRefined,
    IReadOnlyList<BuildJobDto> Queue,
    string Status,
    string? EndReason,
    Guid? WinnerId = null,
    long GnTokensEarned = 0,
    IReadOnlyList<Guid>? UnderAi = null);

/// <summary>The province map's ground: what isn't secret. Veins are entities, so fog hides them (NFR-2b).</summary>
public sealed record MatchMapDto(ZambianProvince Province, int Radius, IReadOnlyList<HexCell> Rock);

/// <summary>Everything a player's client needs to start drawing; later ticks arrive as <see cref="MatchDeltaDto"/>.</summary>
public sealed record MatchStateDto(
    Guid MatchId,
    string Topic,
    PlayerRank Rank,
    MatchMapDto Map,
    MatchDeltaDto State,
    IReadOnlyList<HexCell> Explored,
    DateTimeOffset StartedAt,
    string Mode = MatchModes.Skirmish,
    IReadOnlyList<MatchPlayerDto>? Players = null,
    HexCell? Sector = null);

public static class MatchModes
{
    public const string Skirmish = "skirmish";
    public const string Raid = "raid";
}

public static class MatchSides
{
    public const string Solo = "solo";
    public const string Attacker = "attacker";
    public const string Defender = "defender";
}

public sealed record MatchPlayerDto(Guid UserId, string Name, string Side);

public sealed record StartMatchRequest(ZambianProvince Province);

/// <param name="ActiveMatchId">The most recent unfinished match, if any.</param>
/// <param name="HomeSector">The player's base on the world map (FR-2.1), once picked.</param>
/// <param name="ProtectedUntil">Raids on the home base are refused until then (FR-2.3a).</param>
/// <param name="Matches">Every unfinished match the player is in: their skirmish, and raids they lead or defend.</param>
public sealed record CyberWarsMeDto(
    Guid UserId, PlayerRank Rank, long GnTokens, ZambianProvince? HomeProvince, Guid? ActiveMatchId,
    HexCell? HomeSector = null, AllianceDto? Alliance = null, DateTimeOffset? ProtectedUntil = null,
    IReadOnlyList<ActiveMatchDto>? Matches = null);

public sealed record ActiveMatchDto(Guid MatchId, string Mode, string Side, ZambianProvince Province, HexCell? Sector, string? Opponent);

/// <summary>One thing a player can build, and what it takes (FR-1.2).</summary>
/// <param name="Factory">Which queue builds it; each factory builds one item at a time (FR-1.2a).</param>
/// <param name="Requires">Item keys the player must own first.</param>
/// <param name="MinRank">The rank pack that unlocks it.</param>
/// <param name="LockedReason">When set, it can't be built in this phase whatever the player owns.</param>
/// <param name="Attack">Damage per shot; 0 for anything unarmed.</param>
/// <param name="Range">How many hexes away it can hit.</param>
/// <param name="CooldownTicks">Ticks between shots.</param>
/// <param name="Captures">An engineer: takes over the enemy structure it reaches (FR-2.3).</param>
public sealed record BuildItem(
    string Key, string Name, string Tab, string Factory, string Icon, int Cost, int BuildSeconds,
    bool IsStructure, int Sight, int Health, int PowerSupply, int PowerDemand,
    IReadOnlyList<string> Requires, PlayerRank MinRank, string? LockedReason,
    int Attack = 0, int Range = 0, int CooldownTicks = 10, bool Captures = false);

/// <summary>A rig rarity's balance (FR-1.2b): ore extracted per second and armour.</summary>
public sealed record RigBalance(RigRarity Rarity, decimal OrePerSecond, int Armour);

/// <summary>
/// The game rules file: the build catalogue, the tech tree and the balance table, shared so the
/// sidebar and the server agree; the server re-checks everything it is sent.
/// </summary>
public static class CyberWarsRules
{
    public static class Tabs
    {
        public const string Structures = "Structures";
        public const string Rigs = "Rigs and Harvesters";
        public const string Forces = "Infantry and Vehicles";
        public const string Stratagems = "Stratagems";
        public static readonly string[] All = [Structures, Rigs, Forces, Stratagems];
    }

    public static class Factories
    {
        public const string Construction = "Construction";
        public const string Refinery = "Refinery";
        public const string DroneBay = "Drone bay";
        public const string Barracks = "Barracks";
        public const string WarFactory = "War Factory";
        public const string Uplink = "Uplink";
    }

    public const string CommandCenter = "command-center";
    public const string PowerNode = "power-node";
    public const string Warehouse = "obw";
    public const string Harvester = "harvester";
    public const string Drone = "recon-drone";
    public const string GuardTower = "guard-tower";
    public const string Outpost = "outpost";
    public const string Squad = "security-squad";
    public const string Engineer = "saboteur";
    public const string Convoy = "convoy";
    public const string CrateKind = "crate";

    /// <summary>A raid lasts this long; then the defender holds (FR-2.3).</summary>
    public const int RaidMinutes = 10;

    /// <summary>After a base is lost, raids on it are refused this long (FR-2.3a).</summary>
    public static readonly TimeSpan HomeProtection = TimeSpan.FromHours(24);

    /// <summary>Away this long and the AI starts defending for the player (FR-2.4).</summary>
    public static readonly TimeSpan AwayAfter = TimeSpan.FromSeconds(30);

    /// <summary>The AI defends an absent player's base for at most this long (FR-2.4).</summary>
    public static readonly TimeSpan AiDefendsFor = TimeSpan.FromMinutes(10);

    /// <summary>Command Centers and outposts can be captured but not shot down.</summary>
    public static bool Indestructible(string key) => key is CommandCenter or Outpost;

    /// <summary>The scrap a destroyed rig or harvester leaves (FR-2.3b): play credits only, never real assets.</summary>
    public static ScrapCrate? ScrapFrom(string key, decimal load) => key switch
    {
        Harvester => new ScrapCrate(CrateContents.OreCredits, 150 + (int)Math.Round(load * 2)),
        _ when RigRarityOf(key) is { } rarity => new ScrapCrate(CrateContents.GnTokens, 25 * ((int)rarity + 1)),
        _ => null,
    };

    public static string RigKey(RigRarity rarity) => "rig." + rarity.ToString().ToLowerInvariant();

    public const decimal StartingCredits = 2500;

    /// <summary>Ore credits refined to win a solo skirmish.</summary>
    public const decimal SkirmishGoal = 10_000;

    public const int MatchMinutes = 45;

    /// <summary>How far from one of the player's structures a new one may be placed.</summary>
    public const int BuildRadius = 5;

    public const int HarvesterCapacity = 100;

    /// <summary>Ore a harvester digs from a bare vein each second; a rig's stock loads much faster.</summary>
    public const int BareVeinLoadPerSecond = 5;

    public const int RigLoadPerSecond = 40;

    public const int RigStockCap = 300;

    /// <summary>Ore credits per unit of ore unloaded at an Ore Bonded Warehouse.</summary>
    public static decimal CreditsPerOre(bool gold) => gold ? 3m : 2m;

    /// <summary>Ticks a unit takes to cross one hex.</summary>
    public static int TicksPerHex(string key) => key == Drone ? 2 : 4;

    /// <summary>Below demand, building runs at this fraction of its speed.</summary>
    public const double LowPowerBuildSpeed = 0.5;

    /// <summary>Refused orders in one minute that end a player's match (NFR-2a).</summary>
    public const int RefusalLimit = 20;

    /// <summary>The balance table (FR-1.2b).</summary>
    public static readonly IReadOnlyList<RigBalance> Rigs =
    [
        new(RigRarity.Common, 4m, 200),
        new(RigRarity.Rare, 6m, 350),
        new(RigRarity.Epic, 8m, 500),
        new(RigRarity.Legendary, 10m, 700),
    ];

    public static RigBalance Rig(RigRarity rarity) => Rigs.Single(r => r.Rarity == rarity);

    public static RigRarity? RigRarityOf(string key) =>
        Enum.GetValues<RigRarity>().Cast<RigRarity?>().FirstOrDefault(r => RigKey(r!.Value) == key);

    /// <summary>NodeBoss rigs a player may run at once: one without a pack (FR-1.1d).</summary>
    public static int RigSlots(PlayerRank rank) => rank switch
    {
        PlayerRank.Explorer => 2,
        PlayerRank.Commander => 8,
        PlayerRank.Sovereign => 25,
        _ => 1,
    };

    const string Phase03 = "Arrives with the Derivatives War";

    public static readonly IReadOnlyList<BuildItem> Catalogue =
    [
        new(CommandCenter, "NodeBoss Command Center", Tabs.Structures, Factories.Construction, "🏛️", 0, 0, true, 5, 1500, 20, 0, [], PlayerRank.None, "You start with one"),
        new(PowerNode, "Power Node (solar)", Tabs.Structures, Factories.Construction, "🔆", 300, 6, true, 3, 400, 100, 0, [CommandCenter], PlayerRank.None, null),
        new(Warehouse, "Ore Bonded Warehouse", Tabs.Structures, Factories.Construction, "🏦", 800, 10, true, 4, 900, 0, 40, [PowerNode], PlayerRank.None, null),
        new("aurion-terminal", "Aurion Terminal", Tabs.Structures, Factories.Construction, "📡", 1000, 12, true, 8, 600, 0, 60, [Warehouse], PlayerRank.Commander, null),
        new("barracks", "Barracks", Tabs.Structures, Factories.Construction, "🛡️", 500, 8, true, 3, 700, 0, 20, [PowerNode], PlayerRank.None, null),
        new("war-factory", "War Factory", Tabs.Structures, Factories.Construction, "🏭", 1200, 14, true, 3, 1000, 0, 50, [Warehouse], PlayerRank.None, null),
        new(GuardTower, "Guard tower", Tabs.Structures, Factories.Construction, "🗼", 600, 8, true, 4, 800, 0, 25, ["barracks"], PlayerRank.None, null, Attack: 22, Range: 3, CooldownTicks: 10),
        new(Outpost, "Alliance outpost", Tabs.Structures, Factories.Construction, "⛺", 0, 0, true, 5, 1200, 20, 0, [], PlayerRank.None, "Raiders start with one"),

        new(RigKey(RigRarity.Common), "NodeBoss rig · Common", Tabs.Rigs, Factories.Construction, "⛏️", 500, 6, true, 3, Rig(RigRarity.Common).Armour, 0, 20, [CommandCenter], PlayerRank.None, null),
        new(RigKey(RigRarity.Rare), "NodeBoss rig · Rare", Tabs.Rigs, Factories.Construction, "⛏️", 800, 8, true, 3, Rig(RigRarity.Rare).Armour, 0, 25, [CommandCenter], PlayerRank.Commander, null),
        new(RigKey(RigRarity.Epic), "NodeBoss rig · Epic", Tabs.Rigs, Factories.Construction, "⛏️", 1100, 10, true, 3, Rig(RigRarity.Epic).Armour, 0, 30, [CommandCenter], PlayerRank.Commander, null),
        new(RigKey(RigRarity.Legendary), "NodeBoss rig · Legendary", Tabs.Rigs, Factories.Construction, "⛏️", 1500, 12, true, 3, Rig(RigRarity.Legendary).Armour, 0, 35, [CommandCenter], PlayerRank.Sovereign, null),
        new(Harvester, "Harvester", Tabs.Rigs, Factories.Refinery, "🚚", 600, 6, false, 3, 300, 0, 0, [Warehouse], PlayerRank.None, null),

        new(Drone, "Recon drone", Tabs.Forces, Factories.DroneBay, "🛸", 150, 3, false, 6, 60, 0, 0, [CommandCenter], PlayerRank.None, null),
        new(Squad, "Security squad", Tabs.Forces, Factories.Barracks, "💂", 200, 5, false, 3, 120, 0, 0, ["barracks"], PlayerRank.None, null, Attack: 12, Range: 1, CooldownTicks: 8),
        new(Engineer, "Saboteur engineer", Tabs.Forces, Factories.Barracks, "🧰", 400, 6, false, 3, 80, 0, 0, ["barracks"], PlayerRank.None, null, Captures: true),
        new(Convoy, "Armoured convoy", Tabs.Forces, Factories.WarFactory, "🚛", 900, 10, false, 3, 600, 0, 0, ["war-factory"], PlayerRank.None, null, Attack: 30, Range: 2, CooldownTicks: 12),

        new("liquidity-freeze", "Liquidity Freeze", Tabs.Stratagems, Factories.Uplink, "🧊", 0, 0, false, 0, 0, 0, 0, ["aurion-terminal"], PlayerRank.Commander, Phase03),
        new("supply-sabotage", "Supply Chain Sabotage", Tabs.Stratagems, Factories.Uplink, "💥", 0, 0, false, 0, 0, 0, 0, ["aurion-terminal"], PlayerRank.Commander, Phase03),
        new("flywheel-overdrive", "BTC/Gold Flywheel Overdrive", Tabs.Stratagems, Factories.Uplink, "🌀", 0, 0, false, 0, 0, 0, 0, ["aurion-terminal"], PlayerRank.Commander, Phase03),
    ];

    public static BuildItem? Item(string? key) => Catalogue.FirstOrDefault(i => i.Key == key);

    /// <summary>The buildings that can run a factory's queue; a raider's outpost runs a Barracks and a drone bay.</summary>
    public static IReadOnlyList<string> FactorySources(string factory) => factory switch
    {
        Factories.Construction => [CommandCenter],
        Factories.DroneBay => [CommandCenter, Outpost],
        Factories.Refinery => [Warehouse],
        Factories.Barracks => ["barracks", Outpost],
        Factories.WarFactory => ["war-factory"],
        _ => [],
    };

    /// <summary>Why the player can't build the item now, or null if they can (FR-1.2's "what unlocks them").</summary>
    public static string? Locked(BuildItem item, PlayerRank rank, IReadOnlySet<string> owned)
    {
        if (item.LockedReason is { } reason)
            return reason;
        if (rank < item.MinRank)
            return $"Unlocks with the {item.MinRank} pack";
        // A raider's outpost stands in for a Barracks.
        if (item.Requires.FirstOrDefault(r => !owned.Contains(r) && !(r == "barracks" && owned.Contains(Outpost))) is { } missing)
            return $"Unlocked by building the {Item(missing)?.Name ?? missing}";
        return null;
    }
}

/// <summary>What a scrap crate can hold. Play credits only: there is deliberately no member for NDX, Coop Coin or Believe Points (section 8).</summary>
public enum CrateContents { OreCredits, GnTokens }

public sealed record ScrapCrate(CrateContents Contents, int Amount);

// ---- Phase 02: the world map, alliances and raids ----

public static class CyberWarsWorld
{
    /// <summary>Each province is a hex of sectors this many rings out from its centre: 19 sectors.</summary>
    public const int SectorRadius = 2;

    public static IEnumerable<HexCell> Sectors => new HexCell(0, 0).Within(SectorRadius);

    public static bool IsSector(HexCell sector) => HexCell.Distance(sector, new HexCell(0, 0)) <= SectorRadius;

    public static string ProvinceName(ZambianProvince p) => p == ZambianProvince.NorthWestern ? "North-Western" : p.ToString();

    public static string SectorName(ZambianProvince p, HexCell sector) => $"{ProvinceName(p)} {sector.Q},{sector.R}";

    /// <summary>The game link that opens CyberWars centred on a sector (FR-2.2a).</summary>
    public static string SectorRoute(ZambianProvince province, HexCell sector) => $"apps/cyberwars?sector={province}:{sector.Q}:{sector.R}";

    public static bool TryParseSector(string? text, out ZambianProvince province, out HexCell sector)
    {
        (province, sector) = (default, default);
        var parts = text?.Split(':');
        if (parts is not [var p, var q, var r] || !Enum.TryParse(p, true, out province) || !Enum.IsDefined(province)
            || !int.TryParse(q, out var qq) || !int.TryParse(r, out var rr))
            return false;
        sector = new HexCell(qq, rr);
        return IsSector(sector);
    }
}

/// <param name="HolderId">Whose home base stands in the sector; a sector holds at most one (FR-2.1).</param>
/// <param name="ControllerId">Who holds the sector's Command Center now: the holder, or the raider who captured it.</param>
/// <param name="AllianceId">The controlling alliance, if the controller is in one (FR-2.1a).</param>
public sealed record SectorDto(
    ZambianProvince Province, HexCell Sector, Guid? HolderId, string? HolderName, Guid? ControllerId, string? ControllerName,
    Guid? AllianceId, string? AllianceName, DateTimeOffset? ProtectedUntil, Guid? RaidMatchId);

public sealed record ProvinceMapDto(ZambianProvince Province, IReadOnlyList<SectorDto> Sectors);

public sealed record WorldMapDto(IReadOnlyList<ProvinceMapDto> Provinces);

public sealed record ClaimHomeRequest(ZambianProvince Province, HexCell Sector);

public sealed record StartRaidRequest(ZambianProvince Province, HexCell Sector);

public sealed record AllianceMemberDto(Guid UserId, string Name, bool Leader);

/// <param name="ConversationId">The war room: a group chat in Ndeipi Chat (FR-2.2).</param>
public sealed record AllianceDto(
    Guid Id, string Name, ZambianProvince Province, Guid ConversationId, IReadOnlyList<AllianceMemberDto> Members, StrikeDto? Strike);

/// <summary>A /vote-strike on a sector: approved once more than half the alliance has voted.</summary>
public sealed record StrikeDto(ZambianProvince Province, HexCell Sector, int Votes, int Needed, DateTimeOffset? ApprovedUntil);

public sealed record CreateAllianceRequest(string Name);

/// <summary>A point pinged to the war room (FR-2.2a): the spec's request plus the province it is in.</summary>
public sealed record AlliancePingRequest(HexCell At, string Message, ZambianProvince Province);

public static class MapEventKinds
{
    public const string Sector = "sector";
    public const string Ping = "ping";
    public const string RaidStarted = "raid-started";
    public const string RaidEnded = "raid-ended";
}

/// <summary>What the cyberwars:map:{province} topic carries: sector owners, pings and raids.</summary>
public sealed record MapEventDto(string Kind, ZambianProvince Province, HexCell At, SectorDto? Sector, string? Text, Guid? MatchId);

/// <summary>
/// A game card in a chat (MessageKinds.CyberWarsCard): a ping, a raid alert, a command's answer,
/// or a /send-coop request that opens the wallet's own confirm screen. Opening it never moves value.
/// </summary>
/// <param name="Route">Where tapping it goes: the game (apps/cyberwars?...) or the chat's transfer screen.</param>
public sealed record CyberWarsCardPayload(string Icon, string Title, string Text, string Route, string Action);
