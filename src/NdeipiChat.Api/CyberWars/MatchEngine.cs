using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>
/// Everything a match needs to resume: snapshotted to SQL every 10 s (NFR-3a) and when the players
/// leave, so reopening shows the base as the server last saved it (FR-1.4).
/// </summary>
public sealed class MatchState
{
    public Guid MatchId { get; set; }
    public string Mode { get; set; } = MatchModes.Skirmish;
    public ZambianProvince Province { get; set; }

    /// <summary>The raided sector on the world map; null for a skirmish.</summary>
    public HexCell? Sector { get; set; }

    public DateOnly Day { get; set; }
    public int Radius { get; set; }
    public long Tick { get; set; }
    public long EndsAtTick { get; set; } = CyberWarsRules.MatchMinutes * 60L * CyberWarsContract.TicksPerSecond;
    public string Status { get; set; } = MatchStatuses.Running;
    public string? EndReason { get; set; }
    public Guid? WinnerId { get; set; }
    public List<HexCell> Rock { get; set; } = [];
    public List<MatchEntity> Entities { get; set; } = [];
    public List<MatchPlayer> Players { get; set; } = [];
}

public sealed class MatchPlayer
{
    public Guid UserId { get; set; }
    public string Name { get; set; } = "";
    public string Side { get; set; } = MatchSides.Solo;
    public PlayerRank Rank { get; set; }
    public decimal Credits { get; set; }
    public decimal Refined { get; set; }

    /// <summary>GN Tokens picked up from scrap crates; added to the account when the match ends.</summary>
    public long GnTokens { get; set; }

    public List<BuildJob> Queue { get; set; } = [];
    public List<HexCell> Explored { get; set; } = [];

    /// <summary>The last tick the player's client asked about the match: how the server tells they're away (FR-2.4).</summary>
    public long LastSeenTick { get; set; }

    /// <summary>The head job is waiting for credits and has said so once.</summary>
    public bool ToldNoFunds { get; set; }
}

public sealed class BuildJob
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string Factory { get; set; } = "";
    public double Progress { get; set; }
    public bool Started { get; set; }
    public bool Ready { get; set; }
}

public static class UnitTasks
{
    public const string Idle = "idle";
    public const string Moving = "moving";
    public const string AttackMove = "attack-move";
    public const string Attacking = "attacking";
    public const string Capturing = "capturing";
    public const string ToVein = "to-vein";
    public const string Loading = "loading";
    public const string ToWarehouse = "to-warehouse";
}

public sealed class MatchEntity
{
    public Guid Id { get; set; }
    public Guid OwnerId { get; set; }
    public string Kind { get; set; } = "";
    public HexCell At { get; set; }
    public int Health { get; set; }

    // Units.
    public List<HexCell> Path { get; set; } = [];
    public int MoveTicks { get; set; }
    public string Task { get; set; } = UnitTasks.Idle;
    public Guid? VeinId { get; set; }
    public decimal Load { get; set; }
    public bool LoadIsGold { get; set; }

    // Fighting: what it was told to shoot or take over, and ticks until it can shoot again.
    public Guid? TargetId { get; set; }
    public int Cooldown { get; set; }
    public bool Fired { get; set; }

    // Veins hold Ore; rigs hold Ore as their stock and VeinId as the vein they stand on; a crate holds Ore of Crate.
    public decimal Ore { get; set; }
    public bool Gold { get; set; }
    public CrateContents? Crate { get; set; }

    public bool IsVein => Kind is VeinGold or VeinCopper;
    public bool IsCrate => Kind == CyberWarsRules.CrateKind;
    public bool IsNeutral => OwnerId == Guid.Empty;

    public const string VeinGold = "vein.gold";
    public const string VeinCopper = "vein.copper";
}

/// <summary>A structure of a player's home base, relative to its Command Center; a raid rebuilds the base from these.</summary>
public sealed record BaseStructure(string Kind, int DQ, int DR);

public sealed record RaidSide(Guid UserId, string Name, PlayerRank Rank);

/// <summary>
/// One match's authoritative simulation (NFR-2): orders are checked when they arrive and applied at
/// the next tick; harvest yields, build progress, combat and every balance change happen only in
/// <see cref="Step"/>. Not thread-safe: <see cref="CyberWarsMatches"/> holds a lock around it.
/// </summary>
public sealed class MatchEngine
{
    public MatchState State { get; }

    readonly List<(Guid UserId, RtsOrder Order)> _pending = [];
    readonly Dictionary<Guid, OrderResultDto> _results = [];
    readonly Dictionary<Guid, Queue<DateTimeOffset>> _refusals = [];
    readonly Dictionary<Guid, Dictionary<Guid, EntityDto>> _sent = [];
    readonly Dictionary<Guid, List<string>> _cues = [];
    readonly Dictionary<Guid, HashSet<HexCell>> _explored = [];
    readonly HashSet<HexCell> _rock;
    readonly HashSet<HexCell> _cells;

    static readonly long AwayTicks = (long)CyberWarsRules.AwayAfter.TotalSeconds * CyberWarsContract.TicksPerSecond;
    static readonly long AiTicks = (long)CyberWarsRules.AiDefendsFor.TotalSeconds * CyberWarsContract.TicksPerSecond;

    public MatchEngine(MatchState state)
    {
        State = state;
        _rock = [.. state.Rock];
        _cells = [.. new HexCell(0, 0).Within(state.Radius)];
        foreach (var p in state.Players)
        {
            _explored[p.UserId] = [.. p.Explored];
            _cues[p.UserId] = [];
        }
        RevealAll();
    }

    public bool IsOver => State.Status != MatchStatuses.Running;

    public bool IsRaid => State.Mode == MatchModes.Raid;

    public MatchPlayer? Player(Guid userId) => State.Players.FirstOrDefault(p => p.UserId == userId);

    MatchPlayer? Attacker => State.Players.FirstOrDefault(p => p.Side == MatchSides.Attacker);
    MatchPlayer? Defender => State.Players.FirstOrDefault(p => p.Side == MatchSides.Defender);

    // ---- New matches ----

    /// <summary>
    /// A solo skirmish on one province map. The ground and the veins come from (province, day), so
    /// two matches started the same day in the same province are the same (FR-1.3a).
    /// </summary>
    public static MatchEngine NewSkirmish(Guid matchId, ZambianProvince province, DateOnly day, Guid userId, PlayerRank rank, string name = "")
    {
        const int radius = 12;
        var random = new Random(StableSeed($"{province}:{day:yyyy-MM-dd}"));
        var home = new HexCell(-7, 4);
        var cells = new HexCell(0, 0).Within(radius).ToList();
        var keepClear = home.Within(3).ToHashSet();

        // Vein fields: a copper field close to home, the rest out in the fog, gold the furthest out.
        var fields = new List<(HexCell Centre, bool Gold)> { (new HexCell(-3, 2), false) };
        var candidates = cells.Where(c => HexCell.Distance(c, home) >= 8 && HexCell.Distance(c, new HexCell(0, 0)) <= radius - 2).ToList();
        for (var i = 0; i < 4 && candidates.Count > 0; i++)
        {
            var centre = candidates[random.Next(candidates.Count)];
            fields.Add((centre, i % 2 == 0));
            candidates.RemoveAll(c => HexCell.Distance(c, centre) < 6);
        }
        var veinCells = fields.SelectMany(f => f.Centre.Within(1).Select(c => (Cell: c, f.Gold))).Where(v => cells.Contains(v.Cell)).ToList();
        var veinSet = veinCells.Select(v => v.Cell).ToHashSet();

        var rock = cells.Where(c => !keepClear.Contains(c) && !veinSet.Contains(c) && HexCell.Distance(c, home) > 3 && random.NextDouble() < 0.10).ToList();

        var state = new MatchState { MatchId = matchId, Province = province, Day = day, Radius = radius, Rock = rock };
        AddVeins(state, random, veinCells);
        state.Players.Add(new MatchPlayer { UserId = userId, Name = name, Rank = rank, Credits = CyberWarsRules.StartingCredits });
        state.Entities.Add(NewOf(CyberWarsRules.CommandCenter, userId, home));
        state.Entities.Add(NewOf(CyberWarsRules.Drone, userId, new HexCell(home.Q + 1, home.R - 1)));
        return new MatchEngine(state);
    }

    public static readonly HexCell DefenderHome = new(-7, 4);
    public static readonly HexCell AttackerHome = new(7, -4);

    /// <summary>The base a player gets before they've saved one of their own.</summary>
    public static readonly IReadOnlyList<BaseStructure> DefaultBase =
    [
        new(CyberWarsRules.CommandCenter, 0, 0), new(CyberWarsRules.PowerNode, -1, 1), new(CyberWarsRules.Warehouse, 1, 1),
        new("barracks", -1, 0), new(CyberWarsRules.GuardTower, 2, -1),
    ];

    /// <summary>
    /// A raid (FR-2.3): the attacker lands an alliance outpost and a strike force on the far side of
    /// the defender's home sector, which is rebuilt from their saved base. The ground comes from the
    /// sector, so the same sector is always the same battlefield.
    /// </summary>
    public static MatchEngine NewRaid(Guid matchId, ZambianProvince province, HexCell sector, RaidSide attacker, RaidSide defender,
        IReadOnlyList<BaseStructure>? defenderBase, bool strikeApproved)
    {
        const int radius = 12;
        var random = new Random(StableSeed($"raid:{province}:{sector.Q}:{sector.R}"));
        var cells = new HexCell(0, 0).Within(radius).ToList();
        var keepClear = DefenderHome.Within(3).Concat(AttackerHome.Within(3)).ToHashSet();
        var fields = new List<(HexCell Centre, bool Gold)> { (new HexCell(-3, 1), false), (new HexCell(3, -1), false), (new HexCell(0, 7), true), (new HexCell(0, -7), true) };
        var veinCells = fields.SelectMany(f => f.Centre.Within(1).Select(c => (Cell: c, f.Gold))).Where(v => cells.Contains(v.Cell) && !keepClear.Contains(v.Cell)).ToList();
        var veinSet = veinCells.Select(v => v.Cell).ToHashSet();
        var rock = cells.Where(c => !keepClear.Contains(c) && !veinSet.Contains(c) && random.NextDouble() < 0.08).ToList();

        var state = new MatchState
        {
            MatchId = matchId, Mode = MatchModes.Raid, Province = province, Sector = sector, Radius = radius, Rock = rock,
            EndsAtTick = CyberWarsRules.RaidMinutes * 60L * CyberWarsContract.TicksPerSecond
        };
        AddVeins(state, random, veinCells);

        // The defender's base, as they left it; anything that no longer fits is left out.
        state.Players.Add(new MatchPlayer
        {
            UserId = defender.UserId, Name = defender.Name, Rank = defender.Rank, Side = MatchSides.Defender, Credits = 1000,
            LastSeenTick = -AwayTicks - 1
        });
        var taken = new HashSet<HexCell>();
        var layout = defenderBase is { Count: > 0 } saved && saved.Any(s => s.Kind == CyberWarsRules.CommandCenter) ? saved : DefaultBase;
        foreach (var part in layout.DistinctBy(p => (p.DQ, p.DR)))
        {
            var at = new HexCell(DefenderHome.Q + part.DQ, DefenderHome.R + part.DR);
            if (CyberWarsRules.Item(part.Kind) is not { IsStructure: true } || !cells.Contains(at) || veinSet.Contains(at) || !taken.Add(at))
                continue;
            if (CyberWarsRules.RigRarityOf(part.Kind) is not null)
                continue; // Rigs stand on the defender's own veins, which aren't on this map.
            state.Entities.Add(NewOf(part.Kind, defender.UserId, at));
        }
        if (!state.Entities.Any(e => e.Kind == CyberWarsRules.GuardTower))
            state.Entities.Add(NewOf(CyberWarsRules.GuardTower, defender.UserId, FirstFree(state, cells, taken, DefenderHome, 2)));
        for (var i = 0; i < 3; i++)
            state.Entities.Add(NewOf(CyberWarsRules.Squad, defender.UserId, FirstFree(state, cells, taken, DefenderHome, 1)));
        var truck = NewOf(CyberWarsRules.Harvester, defender.UserId, FirstFree(state, cells, taken, DefenderHome, 2));
        state.Entities.Add(truck);

        // The raiders' landing.
        state.Players.Add(new MatchPlayer
        {
            UserId = attacker.UserId, Name = attacker.Name, Rank = attacker.Rank, Side = MatchSides.Attacker,
            Credits = 1500 + (strikeApproved ? 1000 : 0)
        });
        taken.Add(AttackerHome);
        state.Entities.Add(NewOf(CyberWarsRules.Outpost, attacker.UserId, AttackerHome));
        foreach (var key in Enumerable.Repeat(CyberWarsRules.Squad, strikeApproved ? 6 : 4).Concat([CyberWarsRules.Engineer, CyberWarsRules.Engineer, CyberWarsRules.Drone]))
            state.Entities.Add(NewOf(key, attacker.UserId, FirstFree(state, cells, taken, AttackerHome, 2)));

        var engine = new MatchEngine(state);
        engine.AutoHarvest(truck);
        return engine;
    }

    static HexCell FirstFree(MatchState state, List<HexCell> cells, HashSet<HexCell> taken, HexCell near, int radius)
    {
        var rock = state.Rock.ToHashSet();
        var veins = state.Entities.Where(e => e.IsVein).Select(e => e.At).ToHashSet();
        for (var r = radius; r <= radius + 3; r++)
            foreach (var c in near.Within(r).Where(c => c != near).OrderBy(c => HexCell.Distance(c, near)).ThenBy(c => c.Q).ThenBy(c => c.R))
                if (cells.Contains(c) && !rock.Contains(c) && !veins.Contains(c) && taken.Add(c))
                    return c;
        return near;
    }

    static void AddVeins(MatchState state, Random random, List<(HexCell Cell, bool Gold)> veinCells)
    {
        foreach (var (cell, gold) in veinCells)
        {
            var ore = Math.Round(PlaceholderRichness(state.Province, gold) * (0.75m + (decimal)random.NextDouble() * 0.5m));
            state.Entities.Add(new MatchEntity { Id = NewId(random), Kind = gold ? MatchEntity.VeinGold : MatchEntity.VeinCopper, At = cell, Ore = ore, Gold = gold });
        }
    }

    /// <summary>
    /// Ore per vein cell until the Proof of Production source is decided (SRS section 13): a fixed
    /// figure per province, so richness stays the same for every match on a day.
    /// </summary>
    public static decimal PlaceholderRichness(ZambianProvince province, bool gold) => (province, gold) switch
    {
        (ZambianProvince.Copperbelt, false) or (ZambianProvince.NorthWestern, false) => 1800,
        (ZambianProvince.Eastern, true) or (ZambianProvince.Northern, true) or (ZambianProvince.Lusaka, true) => 1500,
        (_, true) => 1100,
        _ => 1200,
    };

    static int StableSeed(string text)
    {
        // FNV-1a: string.GetHashCode changes between runs.
        var hash = 2166136261;
        foreach (var c in text)
            hash = (hash ^ c) * 16777619;
        return (int)hash;
    }

    static Guid NewId(Random random)
    {
        var bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    static MatchEntity NewOf(string key, Guid owner, HexCell at) =>
        new() { Id = Guid.NewGuid(), OwnerId = owner, Kind = key, At = at, Health = CyberWarsRules.Item(key)!.Health };

    /// <summary>The player's structures relative to their Command Center, to raid later.</summary>
    public IReadOnlyList<BaseStructure>? BaseOf(Guid userId)
    {
        var cc = State.Entities.FirstOrDefault(e => e.OwnerId == userId && e.Kind == CyberWarsRules.CommandCenter);
        if (cc is null)
            return null;
        return State.Entities.Where(e => e.OwnerId == userId && IsStructure(e) && e.Kind != CyberWarsRules.Outpost)
            .Select(e => new BaseStructure(e.Kind, e.At.Q - cc.At.Q, e.At.R - cc.At.R)).ToList();
    }

    /// <summary>An ally's /reinforce (FR-2.2b): security squads land beside the player's base or outpost.</summary>
    public bool Reinforce(Guid userId, int squads, string cue)
    {
        if (IsOver || !IsRaid || Player(userId) is null)
            return false;
        var home = State.Entities.FirstOrDefault(e => e.OwnerId == userId && e.Kind is CyberWarsRules.Outpost or CyberWarsRules.CommandCenter);
        if (home is null)
            return false;
        for (var i = 0; i < squads; i++)
            State.Entities.Add(NewOf(CyberWarsRules.Squad, userId, FreeNeighbour(home.At)));
        Cue(userId, cue);
        return true;
    }

    // ---- Presence (FR-2.4) ----

    /// <summary>The player's client asked about the match: they're here, and in control.</summary>
    public void Touch(Guid userId)
    {
        if (Player(userId) is { } player)
            player.LastSeenTick = State.Tick;
    }

    bool Away(MatchPlayer p) => State.Tick - p.LastSeenTick > AwayTicks;

    /// <summary>Away, but not for so long that the AI has stopped defending for them.</summary>
    bool UnderAi(MatchPlayer p) => IsRaid && Away(p) && State.Tick - p.LastSeenTick - AwayTicks <= AiTicks;

    /// <summary>Gone longer than the AI covers: their units stand down.</summary>
    bool Abandoned(MatchPlayer p) => IsRaid && Away(p) && !UnderAi(p);

    // ---- Orders ----

    /// <summary>
    /// Checks each order now and queues the accepted ones for the next tick (FR-2.3: answered within
    /// a tick). An order id seen before gets its first answer again and is not applied twice.
    /// </summary>
    public IReadOnlyList<OrderResultDto> Submit(Guid userId, IReadOnlyList<RtsOrder> orders, DateTimeOffset now)
    {
        var results = new List<OrderResultDto>(orders.Count);
        foreach (var order in orders)
        {
            if (_results.TryGetValue(order.OrderId, out var earlier))
            {
                results.Add(earlier);
                continue;
            }
            var reason = Check(userId, order);
            var result = reason is null
                ? new OrderResultDto(order.OrderId, true, State.Tick + 1, null)
                : new OrderResultDto(order.OrderId, false, State.Tick, reason);
            if (reason is null)
                _pending.Add((userId, order));
            else
                CountRefusal(userId, now);
            _results[order.OrderId] = result;
            results.Add(result);
        }
        return results;
    }

    void CountRefusal(Guid userId, DateTimeOffset now)
    {
        if (!_refusals.TryGetValue(userId, out var times))
            _refusals[userId] = times = new();
        times.Enqueue(now);
        while (times.Count > 0 && now - times.Peek() > TimeSpan.FromMinutes(1))
            times.Dequeue();
        if (times.Count >= CyberWarsRules.RefusalLimit && !IsOver)
        {
            var other = State.Players.FirstOrDefault(p => p.UserId != userId);
            End(MatchStatuses.Ended, $"Ended after {CyberWarsRules.RefusalLimit} refused orders in a minute.", other?.UserId);
        }
    }

    string? Check(Guid userId, RtsOrder order)
    {
        if (IsOver)
            return "This match is over.";
        if (Player(userId) is not { } player)
            return "You aren't in this match.";
        // A client can't act on a tick it hasn't seen yet: that's how a modified client tries to
        // move faster than the server allows.
        if (order.ClientTick > State.Tick)
            return $"That order is stamped tick {order.ClientTick}, ahead of the server's {State.Tick}: too fast.";

        switch (order.Kind)
        {
            case OrderKind.Move or OrderKind.AttackMove or OrderKind.Stop or OrderKind.Harvest:
                if (CheckUnits(userId, order) is { } unitProblem)
                    return unitProblem;
                if (order.Kind is OrderKind.Move or OrderKind.AttackMove)
                    return order.Target is { } to && _cells.Contains(to) ? null : "Pick a point on the map.";
                if (order.Kind == OrderKind.Harvest)
                {
                    if (order.UnitIds.Any(id => Find(id)!.Kind != CyberWarsRules.Harvester))
                        return "Only harvesters can harvest.";
                    return HarvestTarget(userId, order, out _);
                }
                return null;

            case OrderKind.Build:
                return CheckBuild(player, order.ItemKey);

            case OrderKind.Place:
                if (player.Queue.FirstOrDefault(j => j.Key == order.ItemKey && j.Ready) is null)
                    return "Nothing like that is ready to place.";
                return order.Target is { } cell ? CheckPlacement(userId, order.ItemKey!, cell) : "Pick where to put it.";

            case OrderKind.Cancel:
                return player.Queue.Any(j => j.Id == order.TargetEntityId) ? null : "That isn't in your build queue.";

            case OrderKind.Attack or OrderKind.Capture:
                if (CheckUnits(userId, order) is { } attackers)
                    return attackers;
                if (order.TargetEntityId is not { } targetId || Find(targetId) is not { } target || !Sees(userId, target.At))
                    return "You can't see that target.";
                if (target.IsNeutral || target.OwnerId == userId)
                    return order.Kind == OrderKind.Attack ? "That isn't an enemy." : "That isn't an enemy building.";
                if (order.Kind == OrderKind.Capture)
                {
                    if (order.UnitIds.Any(id => CyberWarsRules.Item(Find(id)!.Kind) is not { Captures: true }))
                        return "Only saboteur engineers can capture.";
                    return IsStructure(target) ? null : "Only buildings can be captured.";
                }
                if (order.UnitIds.Any(id => CyberWarsRules.Item(Find(id)!.Kind) is not { Attack: > 0 }))
                    return "Some of those units can't attack.";
                return CyberWarsRules.Indestructible(target.Kind) ? "Command Centers can't be destroyed: send an engineer to capture it." : null;

            default:
                return "Stratagems arrive with the Derivatives War.";
        }
    }

    string? CheckUnits(Guid userId, RtsOrder order)
    {
        if (order.UnitIds is not { Count: > 0 and <= 60 })
            return "Select your units first.";
        foreach (var id in order.UnitIds)
        {
            var unit = Find(id);
            if (unit is null)
                return "One of those units is gone.";
            if (unit.OwnerId != userId)
                return "That unit isn't yours.";
            if (CyberWarsRules.Item(unit.Kind) is not { IsStructure: false })
                return "Buildings can't move.";
            // One order per unit per tick: more is a client trying to steer faster than the unit can.
            if (_pending.Any(p => p.Order.UnitIds?.Contains(id) == true))
                return "Too fast: that unit already has an order this tick.";
        }
        return null;
    }

    /// <summary>The vein a harvest order points at; refused if the player can't see it.</summary>
    string? HarvestTarget(Guid userId, RtsOrder order, out MatchEntity? vein)
    {
        vein = null;
        var target = order.TargetEntityId is { } id ? Find(id) : order.Target is { } cell ? VeinAt(cell) : null;
        if (target is null && order.TargetEntityId is null && order.Target is { } empty)
            return Explored(userId, empty) ? "There's no vein there." : "You can't see there.";
        if (target is null || !Explored(userId, target.At))
            return "You can't see there.";
        if (target.Kind.StartsWith("rig.") && target.OwnerId == userId)
            target = target.VeinId is { } v ? Find(v) : null;
        if (target is not { IsVein: true })
            return "There's no vein there.";
        vein = target;
        return null;
    }

    string? CheckBuild(MatchPlayer player, string? key)
    {
        if (CyberWarsRules.Item(key) is not { } item || CyberWarsRules.Indestructible(item.Key))
            return "That can't be built.";
        if (CyberWarsRules.Locked(item, player.Rank, Owned(player.UserId)) is { } locked)
            return locked;
        if (FactoryBuilding(player.UserId, item.Factory) is null)
            return $"You need a {FactoryBuildingName(item.Factory)} to build that.";
        if (player.Queue.Count(j => j.Factory == item.Factory) >= 5)
            return "That queue is full.";
        if (CyberWarsRules.RigRarityOf(item.Key) is not null)
        {
            var rigs = State.Entities.Count(e => e.OwnerId == player.UserId && e.Kind.StartsWith("rig.")) + player.Queue.Count(j => j.Key.StartsWith("rig."));
            var slots = CyberWarsRules.RigSlots(player.Rank);
            if (rigs >= slots)
                return slots == 1 ? "You have 1 rig slot; a rank pack adds more." : $"All {slots} of your rig slots are in use.";
        }
        return null;
    }

    string? CheckPlacement(Guid userId, string key, HexCell cell)
    {
        if (!_cells.Contains(cell) || _rock.Contains(cell))
            return "You can't build on rock.";
        if (!Explored(userId, cell))
            return "You can't see there.";
        if (!State.Entities.Any(e => e.OwnerId == userId && IsStructure(e) && HexCell.Distance(e.At, cell) <= CyberWarsRules.BuildRadius))
            return "Too far from your base.";
        if (State.Entities.Any(e => e.At == cell && IsStructure(e)))
            return "Something is already built there.";
        var vein = VeinAt(cell);
        if (CyberWarsRules.RigRarityOf(key) is not null)
            return vein is null ? "Rigs go on a vein." : null;
        return vein is null ? null : "Only rigs go on a vein.";
    }

    // ---- The tick ----

    /// <summary>One 100 ms step (NFR-1).</summary>
    public void Step()
    {
        if (IsOver)
            return;
        State.Tick++;
        foreach (var (userId, order) in _pending)
            Apply(userId, order);
        _pending.Clear();

        foreach (var player in State.Players)
            BuildFor(player);
        foreach (var rig in State.Entities.Where(e => e.Kind.StartsWith("rig.")).ToList())
            Extract(rig);
        if (IsRaid && State.Tick % 5 == 0)
            foreach (var player in State.Players.Where(UnderAi))
                Defend(player);
        foreach (var unit in State.Entities.Where(e => !e.IsVein && !e.IsCrate && !IsStructure(e)).ToList())
        {
            Chase(unit);
            MoveOne(unit);
            if (unit.Kind == CyberWarsRules.Harvester)
                Harvest(unit);
            else if (unit.Task == UnitTasks.Capturing)
                Capture(unit);
        }
        Fight();
        PickUpCrates();
        State.Entities.RemoveAll(e => (e.IsVein || e.IsCrate) && e.Ore <= 0);
        RevealAll();
        if (!IsOver)
            Judge();
    }

    void Judge()
    {
        if (!IsRaid)
        {
            foreach (var player in State.Players.Where(p => p.Refined >= CyberWarsRules.SkirmishGoal))
            {
                Cue(player.UserId, "Skirmish won: the province's ore is yours.");
                End(MatchStatuses.Won, $"You refined {CyberWarsRules.SkirmishGoal:N0} ore credits.", player.UserId);
                return;
            }
            if (State.Tick >= State.EndsAtTick)
                End(MatchStatuses.Ended, "Time's up.");
            return;
        }

        var (attacker, defender) = (Attacker!, Defender!);
        if (!State.Entities.Any(e => e.OwnerId == defender.UserId && e.Kind == CyberWarsRules.CommandCenter))
        {
            End(MatchStatuses.Won, $"{attacker.Name} captured {defender.Name}'s Command Center.", attacker.UserId);
            return;
        }
        var fighters = State.Entities.Any(e => e.OwnerId == attacker.UserId && CyberWarsRules.Item(e.Kind) is { IsStructure: false, Attack: > 0 } or { Captures: true });
        var cheapest = CyberWarsRules.Item(CyberWarsRules.Squad)!.Cost;
        if (!fighters && attacker.Queue.Count == 0 && attacker.Credits < cheapest
            || !State.Entities.Any(e => e.OwnerId == attacker.UserId && e.Kind == CyberWarsRules.Outpost))
        {
            End(MatchStatuses.Won, $"{defender.Name}'s base drove the raiders off.", defender.UserId);
            return;
        }
        if (Abandoned(attacker))
        {
            End(MatchStatuses.Won, $"{attacker.Name} left the raid; {defender.Name}'s base holds.", defender.UserId);
            return;
        }
        if (State.Tick >= State.EndsAtTick)
            End(MatchStatuses.Won, $"Time's up: {defender.Name}'s base held.", defender.UserId);
    }

    public void End(string status, string reason, Guid? winner = null)
    {
        State.Status = status;
        State.EndReason = reason;
        State.WinnerId = winner;
        _pending.Clear();
        foreach (var p in State.Players)
            Cue(p.UserId, reason);
    }

    void Apply(Guid userId, RtsOrder order)
    {
        var player = Player(userId)!;
        switch (order.Kind)
        {
            case OrderKind.Move or OrderKind.AttackMove:
                foreach (var unit in Units(order))
                {
                    unit.Task = order.Kind == OrderKind.Move ? UnitTasks.Moving : UnitTasks.AttackMove;
                    unit.VeinId = null;
                    unit.TargetId = null;
                    unit.Path = PathTo(unit.At, order.Target!.Value);
                    unit.MoveTicks = 0;
                }
                break;

            case OrderKind.Stop:
                foreach (var unit in Units(order))
                {
                    unit.Task = UnitTasks.Idle;
                    unit.VeinId = null;
                    unit.TargetId = null;
                    unit.Path = [];
                }
                break;

            case OrderKind.Harvest:
                if (HarvestTarget(userId, order, out var vein) is null && vein is not null)
                    foreach (var unit in Units(order))
                        SendToVein(unit, vein);
                break;

            case OrderKind.Attack or OrderKind.Capture:
                if (order.TargetEntityId is not { } targetId || Find(targetId) is not { IsNeutral: false } target || target.OwnerId == userId)
                    break;
                foreach (var unit in Units(order))
                {
                    unit.Task = order.Kind == OrderKind.Attack ? UnitTasks.Attacking : UnitTasks.Capturing;
                    unit.TargetId = target.Id;
                    unit.VeinId = null;
                    unit.Path = PathTo(unit.At, target.At);
                    unit.MoveTicks = 0;
                }
                break;

            case OrderKind.Build:
                // Re-checked: the tick before may have spent a slot.
                if (CheckBuild(player, order.ItemKey) is null)
                {
                    var item = CyberWarsRules.Item(order.ItemKey)!;
                    player.Queue.Add(new BuildJob { Id = order.OrderId, Key = item.Key, Factory = item.Factory });
                }
                break;

            case OrderKind.Place:
                var job = player.Queue.FirstOrDefault(j => j.Key == order.ItemKey && j.Ready);
                if (job is null)
                    break;
                if (CheckPlacement(userId, job.Key, order.Target!.Value) is { } problem)
                {
                    Cue(userId, problem);
                    break;
                }
                player.Queue.Remove(job);
                var built = NewOf(job.Key, userId, order.Target!.Value);
                if (CyberWarsRules.RigRarityOf(job.Key) is not null)
                    built.VeinId = VeinAt(built.At)!.Id;
                State.Entities.Add(built);
                if (job.Key == CyberWarsRules.Warehouse)
                {
                    // Like a refinery, a warehouse comes with its first harvester.
                    var truck = NewOf(CyberWarsRules.Harvester, userId, FreeNeighbour(built.At));
                    State.Entities.Add(truck);
                    AutoHarvest(truck);
                }
                break;

            case OrderKind.Cancel:
                var cancelled = player.Queue.FirstOrDefault(j => j.Id == order.TargetEntityId);
                if (cancelled is null)
                    break;
                player.Queue.Remove(cancelled);
                // FR-1.2a: nothing was charged until it started, so only a started item is refunded.
                if (cancelled.Started)
                    player.Credits += CyberWarsRules.Item(cancelled.Key)!.Cost;
                break;
        }
    }

    void BuildFor(MatchPlayer player)
    {
        var speed = LowPower(player.UserId) ? CyberWarsRules.LowPowerBuildSpeed : 1.0;
        foreach (var factory in player.Queue.Select(j => j.Factory).Distinct().ToList())
        {
            var job = player.Queue.First(j => j.Factory == factory);
            if (job.Ready)
                continue; // The construction yard waits until its structure is placed.
            var source = FactoryBuilding(player.UserId, factory);
            if (source is null)
                continue;
            var item = CyberWarsRules.Item(job.Key)!;
            if (!job.Started)
            {
                if (player.Credits < item.Cost)
                {
                    if (!player.ToldNoFunds)
                        Cue(player.UserId, "Insufficient funds.");
                    player.ToldNoFunds = true;
                    continue;
                }
                // FR-1.2a: charged when it starts, not when queued.
                player.Credits -= item.Cost;
                player.ToldNoFunds = false;
                job.Started = true;
            }
            job.Progress = Math.Min(1, job.Progress + speed / Math.Max(1, item.BuildSeconds * CyberWarsContract.TicksPerSecond));
            if (job.Progress < 1)
                continue;
            if (item.IsStructure)
            {
                job.Ready = true;
                Cue(player.UserId, $"Construction complete: {item.Name}.");
            }
            else
            {
                player.Queue.Remove(job);
                var unit = NewOf(item.Key, player.UserId, FreeNeighbour(source.At));
                State.Entities.Add(unit);
                if (unit.Kind == CyberWarsRules.Harvester)
                    AutoHarvest(unit);
                Cue(player.UserId, $"Unit ready: {item.Name}.");
            }
        }
    }

    void Extract(MatchEntity rig)
    {
        if (rig.VeinId is not { } veinId || Find(veinId) is not { } vein || rig.Ore >= CyberWarsRules.RigStockCap)
            return;
        var rate = CyberWarsRules.Rig(CyberWarsRules.RigRarityOf(rig.Kind)!.Value).OrePerSecond / CyberWarsContract.TicksPerSecond;
        var take = Math.Min(Math.Min(rate, vein.Ore), CyberWarsRules.RigStockCap - rig.Ore);
        vein.Ore -= take;
        rig.Ore += take;
    }

    void MoveOne(MatchEntity unit)
    {
        if (unit.Path.Count == 0)
            return;
        // Attack-move: stop to shoot whatever comes into range.
        if (unit.Task == UnitTasks.AttackMove && NearestEnemy(unit, CyberWarsRules.Item(unit.Kind)!.Range) is not null)
            return;
        if (++unit.MoveTicks < CyberWarsRules.TicksPerHex(unit.Kind))
            return;
        unit.MoveTicks = 0;
        unit.At = unit.Path[0];
        unit.Path.RemoveAt(0);
        if (unit.Path.Count == 0 && unit.Task is UnitTasks.Moving or UnitTasks.AttackMove)
            unit.Task = UnitTasks.Idle;
    }

    /// <summary>Keeps a unit told to attack or capture heading for its target, which may be moving.</summary>
    void Chase(MatchEntity unit)
    {
        if (unit.Task is not (UnitTasks.Attacking or UnitTasks.Capturing))
            return;
        if (unit.TargetId is not { } id || Find(id) is not { } target || target.OwnerId == unit.OwnerId)
        {
            unit.Task = UnitTasks.Idle;
            unit.TargetId = null;
            unit.Path = [];
            return;
        }
        var reach = unit.Task == UnitTasks.Capturing ? 1 : CyberWarsRules.Item(unit.Kind)!.Range;
        if (HexCell.Distance(unit.At, target.At) <= reach)
        {
            unit.Path = [];
            return;
        }
        if (unit.Path.Count == 0 || unit.Path[^1] != target.At && HexCell.Distance(unit.Path[^1], target.At) > 1)
        {
            unit.Path = PathTo(unit.At, target.At);
            unit.MoveTicks = 0;
        }
    }

    /// <summary>An engineer next to its target takes the building over (FR-2.3), and is spent doing it.</summary>
    void Capture(MatchEntity engineer)
    {
        if (engineer.TargetId is not { } id || Find(id) is not { } target || HexCell.Distance(engineer.At, target.At) > 1)
            return;
        var loser = target.OwnerId;
        target.OwnerId = engineer.OwnerId;
        target.TargetId = null;
        State.Entities.Remove(engineer);
        var name = CyberWarsRules.Item(target.Kind)?.Name ?? target.Kind;
        Cue(engineer.OwnerId, $"Building captured: {name}.");
        Cue(loser, $"{name} lost to saboteurs.");
    }

    /// <summary>Every armed unit and tower shoots if it can; the destroyed are cleared and scrap dropped.</summary>
    void Fight()
    {
        foreach (var shooter in State.Entities.Where(e => e.Health > 0 && CyberWarsRules.Item(e.Kind) is { Attack: > 0 }).ToList())
        {
            shooter.Fired = false;
            if (shooter.Cooldown > 0)
            {
                shooter.Cooldown--;
                continue;
            }
            if (Player(shooter.OwnerId) is not { } owner || Abandoned(owner))
                continue;
            var item = CyberWarsRules.Item(shooter.Kind)!;
            MatchEntity? target = null;
            if (shooter.TargetId is { } id && Find(id) is { } chosen && chosen.OwnerId != shooter.OwnerId && !chosen.IsNeutral
                && HexCell.Distance(shooter.At, chosen.At) <= item.Range && !CyberWarsRules.Indestructible(chosen.Kind))
                target = chosen;
            // On a plain move, units hold fire; everything else shoots back at whatever is in range.
            else if (shooter.Task != UnitTasks.Moving)
                target = NearestEnemy(shooter, item.Range);
            if (target is null)
                continue;
            target.Health -= item.Attack;
            shooter.Fired = true;
            var slow = item.IsStructure && LowPower(shooter.OwnerId);
            shooter.Cooldown = item.CooldownTicks * (slow ? 2 : 1);
        }

        foreach (var dead in State.Entities.Where(e => e.Health <= 0 && !e.IsVein && !e.IsCrate).ToList())
        {
            State.Entities.Remove(dead);
            var name = CyberWarsRules.Item(dead.Kind)?.Name ?? dead.Kind;
            Cue(dead.OwnerId, $"{name} destroyed.");
            if (CyberWarsRules.ScrapFrom(dead.Kind, dead.Load) is { } scrap)
                DropCrate(dead.At, scrap);
        }
    }

    /// <summary>FR-2.3b: a scrap crate holds ore credits or GN Tokens, nothing else.</summary>
    void DropCrate(HexCell at, ScrapCrate scrap)
    {
        var existing = State.Entities.FirstOrDefault(e => e.IsCrate && e.At == at && e.Crate == scrap.Contents);
        if (existing is not null)
        {
            existing.Ore += scrap.Amount;
            return;
        }
        State.Entities.Add(new MatchEntity { Id = Guid.NewGuid(), Kind = CyberWarsRules.CrateKind, At = at, Crate = scrap.Contents, Ore = scrap.Amount });
    }

    void PickUpCrates()
    {
        foreach (var crate in State.Entities.Where(e => e.IsCrate && e.Ore > 0).ToList())
        {
            var finder = State.Entities.FirstOrDefault(e => e.At == crate.At && !e.IsNeutral && !IsStructure(e));
            if (finder is null || Player(finder.OwnerId) is not { } player)
                continue;
            var amount = (long)crate.Ore;
            switch (crate.Crate)
            {
                case CrateContents.OreCredits:
                    player.Credits += amount;
                    Cue(player.UserId, $"Scrap crate: {amount:N0} ore credits.");
                    break;
                case CrateContents.GnTokens:
                    player.GnTokens += amount;
                    Cue(player.UserId, $"Scrap crate: {amount:N0} GN Tokens.");
                    break;
            }
            crate.Ore = 0;
        }
    }

    /// <summary>The AI defending an absent player's base (FR-2.4): idle fighters go for raiders near it.</summary>
    void Defend(MatchPlayer player)
    {
        var structures = State.Entities.Where(e => e.OwnerId == player.UserId && IsStructure(e)).ToList();
        var sight = Sight(player.UserId);
        var raiders = State.Entities.Where(e => !e.IsNeutral && e.OwnerId != player.UserId && sight.Contains(e.At)
            && structures.Any(s => HexCell.Distance(s.At, e.At) <= 7)).ToList();
        foreach (var unit in State.Entities.Where(e => e.OwnerId == player.UserId && e.Task == UnitTasks.Idle && CyberWarsRules.Item(e.Kind) is { IsStructure: false, Attack: > 0 }))
        {
            var target = raiders.Where(r => !CyberWarsRules.Indestructible(r.Kind)).MinBy(r => HexCell.Distance(r.At, unit.At));
            if (target is null)
                continue;
            unit.Task = UnitTasks.Attacking;
            unit.TargetId = target.Id;
            unit.Path = PathTo(unit.At, target.At);
            unit.MoveTicks = 0;
        }
    }

    MatchEntity? NearestEnemy(MatchEntity from, int range)
    {
        if (range <= 0)
            return null;
        MatchEntity? best = null;
        var bestScore = int.MaxValue;
        foreach (var e in State.Entities)
        {
            if (e.IsNeutral || e.OwnerId == from.OwnerId || e.Health <= 0 || CyberWarsRules.Indestructible(e.Kind))
                continue;
            var d = HexCell.Distance(from.At, e.At);
            if (d > range)
                continue;
            // Units before buildings, then the closest.
            var score = d + (IsStructure(e) ? 100 : 0);
            if (score < bestScore)
                (best, bestScore) = (e, score);
        }
        return best;
    }

    void Harvest(MatchEntity truck)
    {
        switch (truck.Task)
        {
            case UnitTasks.ToVein when truck.Path.Count == 0:
                var vein = truck.VeinId is { } id ? Find(id) : null;
                if (vein is null || vein.At != truck.At)
                {
                    AutoHarvest(truck);
                    break;
                }
                truck.Task = UnitTasks.Loading;
                truck.LoadIsGold = vein.Gold;
                break;

            case UnitTasks.Loading:
                var here = truck.VeinId is { } veinId ? Find(veinId) : null;
                var rig = State.Entities.FirstOrDefault(e => e.Kind.StartsWith("rig.") && e.At == truck.At && e.OwnerId == truck.OwnerId);
                var room = CyberWarsRules.HarvesterCapacity - truck.Load;
                if (rig is { Ore: > 0 })
                {
                    var take = Math.Min(Math.Min((decimal)CyberWarsRules.RigLoadPerSecond / CyberWarsContract.TicksPerSecond, rig.Ore), room);
                    rig.Ore -= take;
                    truck.Load += take;
                }
                else if (here is { Ore: > 0 })
                {
                    var take = Math.Min(Math.Min((decimal)CyberWarsRules.BareVeinLoadPerSecond / CyberWarsContract.TicksPerSecond, here.Ore), room);
                    here.Ore -= take;
                    truck.Load += take;
                }
                if (truck.Load >= CyberWarsRules.HarvesterCapacity || (here is not { Ore: > 0 } && rig is not { Ore: > 0 }))
                    GoUnload(truck);
                break;

            case UnitTasks.ToWarehouse when truck.Path.Count == 0:
                var warehouse = State.Entities.FirstOrDefault(e => e.OwnerId == truck.OwnerId && e.Kind == CyberWarsRules.Warehouse && HexCell.Distance(e.At, truck.At) <= 1);
                if (warehouse is null)
                {
                    GoUnload(truck);
                    break;
                }
                Unload(truck);
                if (truck.VeinId is { } back && Find(back) is { } same)
                    SendToVein(truck, same);
                else
                    AutoHarvest(truck);
                break;
        }
    }

    /// <summary>FR-1.3: the only place ore credits rise, besides a scrap crate's.</summary>
    void Unload(MatchEntity truck)
    {
        var player = Player(truck.OwnerId)!;
        var credits = Math.Round(truck.Load * CyberWarsRules.CreditsPerOre(truck.LoadIsGold), 2);
        player.Credits += credits;
        player.Refined += credits;
        truck.Load = 0;
    }

    void SendToVein(MatchEntity truck, MatchEntity vein)
    {
        truck.VeinId = vein.Id;
        truck.Task = UnitTasks.ToVein;
        truck.Path = PathTo(truck.At, vein.At);
        truck.MoveTicks = 0;
    }

    void GoUnload(MatchEntity truck)
    {
        var warehouse = State.Entities.Where(e => e.OwnerId == truck.OwnerId && e.Kind == CyberWarsRules.Warehouse)
            .OrderBy(e => HexCell.Distance(e.At, truck.At)).FirstOrDefault();
        if (warehouse is null)
        {
            truck.Task = UnitTasks.Idle;
            Cue(truck.OwnerId, "Build an Ore Bonded Warehouse to unload.");
            return;
        }
        truck.Task = UnitTasks.ToWarehouse;
        truck.Path = PathTo(truck.At, warehouse.At);
        truck.MoveTicks = 0;
    }

    /// <summary>Sends an idle harvester to the nearest vein its owner has seen.</summary>
    void AutoHarvest(MatchEntity truck)
    {
        if (truck.Load >= CyberWarsRules.HarvesterCapacity)
        {
            GoUnload(truck);
            return;
        }
        var vein = State.Entities.Where(e => e.IsVein && e.Ore > 0 && Explored(truck.OwnerId, e.At))
            .OrderBy(e => HexCell.Distance(e.At, truck.At)).FirstOrDefault();
        if (vein is null)
        {
            if (truck.Load > 0)
                GoUnload(truck);
            else
                truck.Task = UnitTasks.Idle;
            return;
        }
        SendToVein(truck, vein);
    }

    // ---- Geometry, power and sight ----

    static bool IsStructure(MatchEntity e) => CyberWarsRules.Item(e.Kind) is { IsStructure: true };

    bool Blocked(HexCell cell) =>
        !_cells.Contains(cell) || _rock.Contains(cell) || State.Entities.Any(e => e.At == cell && IsStructure(e) && !e.Kind.StartsWith("rig."));

    /// <summary>A shortest walk to the cell, or next to it when it's blocked (a building, say).</summary>
    List<HexCell> PathTo(HexCell from, HexCell to)
    {
        var goals = Blocked(to) ? to.Neighbours().Where(n => !Blocked(n)).ToHashSet() : [to];
        if (goals.Count == 0 || goals.Contains(from))
            return [];
        var came = new Dictionary<HexCell, HexCell> { [from] = from };
        var frontier = new Queue<HexCell>([from]);
        while (frontier.TryDequeue(out var cell))
        {
            foreach (var next in cell.Neighbours())
            {
                if (came.ContainsKey(next) || Blocked(next))
                    continue;
                came[next] = cell;
                if (goals.Contains(next))
                {
                    var path = new List<HexCell> { next };
                    for (var at = cell; at != from; at = came[at])
                        path.Add(at);
                    path.Reverse();
                    return path;
                }
                frontier.Enqueue(next);
            }
        }
        return [];
    }

    HexCell FreeNeighbour(HexCell cell) =>
        cell.Within(2).Where(c => c != cell && !Blocked(c) && VeinAt(c) is null).OrderBy(c => HexCell.Distance(c, cell)).FirstOrDefault(cell);

    MatchEntity? Find(Guid id) => State.Entities.FirstOrDefault(e => e.Id == id);

    MatchEntity? VeinAt(HexCell cell) => State.Entities.FirstOrDefault(e => e.IsVein && e.At == cell);

    IEnumerable<MatchEntity> Units(RtsOrder order) => order.UnitIds.Select(Find).OfType<MatchEntity>();

    HashSet<string> Owned(Guid userId) => [.. State.Entities.Where(e => e.OwnerId == userId).Select(e => e.Kind)];

    MatchEntity? FactoryBuilding(Guid userId, string factory)
    {
        var keys = CyberWarsRules.FactorySources(factory);
        return State.Entities.FirstOrDefault(e => e.OwnerId == userId && keys.Contains(e.Kind));
    }

    static string FactoryBuildingName(string factory) => factory switch
    {
        CyberWarsRules.Factories.Refinery => "Ore Bonded Warehouse",
        CyberWarsRules.Factories.Construction or CyberWarsRules.Factories.DroneBay => "Command Center",
        _ => factory,
    };

    public (int Supply, int Demand) Power(Guid userId)
    {
        int supply = 0, demand = 0;
        foreach (var e in State.Entities.Where(e => e.OwnerId == userId))
            if (CyberWarsRules.Item(e.Kind) is { } item)
                (supply, demand) = (supply + item.PowerSupply, demand + item.PowerDemand);
        return (supply, demand);
    }

    bool LowPower(Guid userId)
    {
        var (supply, demand) = Power(userId);
        return supply < demand;
    }

    /// <summary>The cells the player's units and buildings see right now.</summary>
    public HashSet<HexCell> Sight(Guid userId)
    {
        var seen = new HashSet<HexCell>();
        foreach (var e in State.Entities.Where(e => e.OwnerId == userId))
            if (CyberWarsRules.Item(e.Kind) is { Sight: > 0 } item)
                seen.UnionWith(e.At.Within(item.Sight));
        seen.IntersectWith(_cells);
        return seen;
    }

    bool Sees(Guid userId, HexCell cell) => Sight(userId).Contains(cell);

    bool Explored(Guid userId, HexCell cell) => _explored.TryGetValue(userId, out var cells) && cells.Contains(cell);

    void RevealAll()
    {
        foreach (var player in State.Players)
        {
            var explored = _explored[player.UserId];
            var before = explored.Count;
            explored.UnionWith(Sight(player.UserId));
            if (explored.Count != before)
                player.Explored = [.. explored];
        }
    }

    void Cue(Guid userId, string text)
    {
        if (_cues.TryGetValue(userId, out var cues))
            cues.Add(text);
    }

    // ---- What each player is told ----

    /// <summary>
    /// What changed for the player since the last delta: only entities in their sight (NFR-2b), so a
    /// modified client has nothing hidden to reveal.
    /// </summary>
    public MatchDeltaDto DeltaFor(Guid userId)
    {
        if (!_sent.TryGetValue(userId, out var sent))
            _sent[userId] = sent = [];
        var now = VisibleTo(userId).ToDictionary(e => e.Id);
        var changed = now.Values.Where(e => !sent.TryGetValue(e.Id, out var was) || was != e).ToList();
        var removed = sent.Keys.Where(id => !now.ContainsKey(id)).ToList();
        _sent[userId] = now;
        List<string> cues = _cues.TryGetValue(userId, out var list) ? [.. list] : [];
        list?.Clear();
        return Summary(userId, changed, removed, cues);
    }

    /// <summary>The whole picture as of the last delta, so a client that (re)joins can apply later deltas to it.</summary>
    public MatchDeltaDto CurrentFor(Guid userId)
    {
        if (!_sent.TryGetValue(userId, out var sent))
            _sent[userId] = sent = VisibleTo(userId).ToDictionary(e => e.Id);
        return Summary(userId, [.. sent.Values], [], []);
    }

    MatchDeltaDto Summary(Guid userId, List<EntityDto> changed, List<Guid> removed, List<string> cues)
    {
        var player = Player(userId)!;
        var (supply, demand) = Power(userId);
        var queue = player.Queue.Select(j => new BuildJobDto(j.Id, j.Key, j.Factory, Math.Round(j.Progress, 3), j.Started, j.Ready)).ToList();
        var underAi = State.Players.Where(UnderAi).Select(p => p.UserId).ToList();
        return new MatchDeltaDto(State.MatchId, State.Tick, changed, removed, Math.Round(player.Credits, 2), supply, demand, cues,
            Math.Round(player.Refined, 2), queue, State.Status, State.EndReason, State.WinnerId, player.GnTokens, underAi);
    }

    IEnumerable<EntityDto> VisibleTo(Guid userId)
    {
        var sight = Sight(userId);
        return State.Entities.Where(e => e.OwnerId == userId || sight.Contains(e.At)).Select(ToDto);
    }

    static EntityDto ToDto(MatchEntity e) =>
        new(e.Id, e.OwnerId, e.IsCrate ? $"{CyberWarsRules.CrateKind}.{(e.Crate == CrateContents.GnTokens ? "gn" : "ore")}" : e.Kind, e.At,
            e.IsVein || e.IsCrate ? (int)Math.Ceiling(e.Ore) : e.Health, StateOf(e));

    static string? StateOf(MatchEntity e)
    {
        if (e.Kind.StartsWith("rig."))
            return $"stock {(int)e.Ore}/{CyberWarsRules.RigStockCap}";
        if (e.Kind == CyberWarsRules.Harvester)
            return e.Task switch
            {
                UnitTasks.Loading => $"loading {(int)e.Load}/{CyberWarsRules.HarvesterCapacity}",
                UnitTasks.ToWarehouse => $"hauling {(int)e.Load}/{CyberWarsRules.HarvesterCapacity}",
                UnitTasks.ToVein => "to vein",
                UnitTasks.Moving => "moving",
                _ => e.Load > 0 ? $"idle {(int)e.Load}/{CyberWarsRules.HarvesterCapacity}" : null,
            };
        if (e.Fired)
            return "firing";
        return e.Task switch
        {
            UnitTasks.Attacking => "attacking",
            UnitTasks.Capturing => "capturing",
            _ => e.Path.Count > 0 ? "moving" : null,
        };
    }

    public MatchMapDto Map => new(State.Province, State.Radius, State.Rock);

    public IReadOnlyList<HexCell> ExploredBy(Guid userId) => [.. _explored[userId]];

    public IReadOnlyList<MatchPlayerDto> Roster => [.. State.Players.Select(p => new MatchPlayerDto(p.UserId, p.Name, p.Side))];
}
