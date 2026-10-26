using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>
/// Everything a match needs to resume: snapshotted to SQL every 10 s (NFR-3a) and when the player
/// leaves, so reopening shows the base as the server last saved it (FR-1.4).
/// </summary>
public sealed class MatchState
{
    public Guid MatchId { get; set; }
    public ZambianProvince Province { get; set; }
    public DateOnly Day { get; set; }
    public int Radius { get; set; }
    public long Tick { get; set; }
    public string Status { get; set; } = MatchStatuses.Running;
    public string? EndReason { get; set; }
    public List<HexCell> Rock { get; set; } = [];
    public List<MatchEntity> Entities { get; set; } = [];
    public List<MatchPlayer> Players { get; set; } = [];
}

public sealed class MatchPlayer
{
    public Guid UserId { get; set; }
    public PlayerRank Rank { get; set; }
    public decimal Credits { get; set; }
    public decimal Refined { get; set; }
    public List<BuildJob> Queue { get; set; } = [];
    public List<HexCell> Explored { get; set; } = [];

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

public static class HarvestTasks
{
    public const string Idle = "idle";
    public const string Moving = "moving";
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
    public string Task { get; set; } = HarvestTasks.Idle;
    public Guid? VeinId { get; set; }
    public decimal Load { get; set; }
    public bool LoadIsGold { get; set; }

    // Veins hold Ore; rigs hold Ore as their stock and VeinId as the vein they stand on.
    public decimal Ore { get; set; }
    public bool Gold { get; set; }

    public bool IsVein => Kind is VeinGold or VeinCopper;

    public const string VeinGold = "vein.gold";
    public const string VeinCopper = "vein.copper";
}

/// <summary>
/// One match's authoritative simulation (NFR-2): orders are checked when they arrive and applied at
/// the next tick; harvest yields, build progress and every balance change happen only in
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

    public MatchPlayer? Player(Guid userId) => State.Players.FirstOrDefault(p => p.UserId == userId);

    // ---- A new skirmish ----

    /// <summary>
    /// A solo skirmish on one province map. The ground and the veins come from (province, day), so
    /// two matches started the same day in the same province are the same (FR-1.3a).
    /// </summary>
    public static MatchEngine NewSkirmish(Guid matchId, ZambianProvince province, DateOnly day, Guid userId, PlayerRank rank)
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
        foreach (var (cell, gold) in veinCells)
        {
            var ore = Math.Round(PlaceholderRichness(province, gold) * (0.75m + (decimal)random.NextDouble() * 0.5m));
            state.Entities.Add(new MatchEntity { Id = NewId(random), Kind = gold ? MatchEntity.VeinGold : MatchEntity.VeinCopper, At = cell, Ore = ore, Gold = gold });
        }
        state.Players.Add(new MatchPlayer { UserId = userId, Rank = rank, Credits = CyberWarsRules.StartingCredits });
        state.Entities.Add(NewOf(CyberWarsRules.CommandCenter, userId, home));
        state.Entities.Add(NewOf(CyberWarsRules.Drone, userId, new HexCell(home.Q + 1, home.R - 1)));
        return new MatchEngine(state);
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
            End(MatchStatuses.Ended, $"Ended after {CyberWarsRules.RefusalLimit} refused orders in a minute.");
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
                return "There's no one to fight in a solo skirmish.";

            default:
                return "Stratagems arrive with Alliance Wars.";
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
        if (CyberWarsRules.Item(key) is not { } item || item.Key == CyberWarsRules.CommandCenter)
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
        foreach (var unit in State.Entities.Where(e => !e.IsVein && !IsStructure(e)).ToList())
        {
            MoveOne(unit);
            if (unit.Kind == CyberWarsRules.Harvester)
                Harvest(unit);
        }
        State.Entities.RemoveAll(e => e.IsVein && e.Ore <= 0);
        RevealAll();

        foreach (var player in State.Players.Where(p => p.Refined >= CyberWarsRules.SkirmishGoal))
        {
            Cue(player.UserId, "Skirmish won: the province's ore is yours.");
            End(MatchStatuses.Won, $"You refined {CyberWarsRules.SkirmishGoal:N0} ore credits.");
        }
        if (!IsOver && State.Tick >= CyberWarsRules.MatchMinutes * 60L * CyberWarsContract.TicksPerSecond)
            End(MatchStatuses.Ended, "Time's up.");
    }

    public void End(string status, string reason)
    {
        State.Status = status;
        State.EndReason = reason;
        _pending.Clear();
    }

    void Apply(Guid userId, RtsOrder order)
    {
        var player = Player(userId)!;
        switch (order.Kind)
        {
            case OrderKind.Move or OrderKind.AttackMove:
                foreach (var unit in Units(order))
                {
                    unit.Task = HarvestTasks.Moving;
                    unit.VeinId = null;
                    unit.Path = PathTo(unit.At, order.Target!.Value);
                    unit.MoveTicks = 0;
                }
                break;

            case OrderKind.Stop:
                foreach (var unit in Units(order))
                {
                    unit.Task = HarvestTasks.Idle;
                    unit.VeinId = null;
                    unit.Path = [];
                }
                break;

            case OrderKind.Harvest:
                if (HarvestTarget(userId, order, out var vein) is null && vein is not null)
                    foreach (var unit in Units(order))
                        SendToVein(unit, vein);
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
        var (supply, demand) = Power(player.UserId);
        var speed = supply >= demand ? 1.0 : CyberWarsRules.LowPowerBuildSpeed;
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
        if (++unit.MoveTicks < CyberWarsRules.TicksPerHex(unit.Kind))
            return;
        unit.MoveTicks = 0;
        unit.At = unit.Path[0];
        unit.Path.RemoveAt(0);
        if (unit.Path.Count == 0 && unit.Task == HarvestTasks.Moving)
            unit.Task = HarvestTasks.Idle;
    }

    void Harvest(MatchEntity truck)
    {
        switch (truck.Task)
        {
            case HarvestTasks.ToVein when truck.Path.Count == 0:
                var vein = truck.VeinId is { } id ? Find(id) : null;
                if (vein is null || vein.At != truck.At)
                {
                    AutoHarvest(truck);
                    break;
                }
                truck.Task = HarvestTasks.Loading;
                truck.LoadIsGold = vein.Gold;
                break;

            case HarvestTasks.Loading:
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

            case HarvestTasks.ToWarehouse when truck.Path.Count == 0:
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

    /// <summary>FR-1.3: the only place ore credits rise.</summary>
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
        truck.Task = HarvestTasks.ToVein;
        truck.Path = PathTo(truck.At, vein.At);
        truck.MoveTicks = 0;
    }

    void GoUnload(MatchEntity truck)
    {
        var warehouse = State.Entities.Where(e => e.OwnerId == truck.OwnerId && e.Kind == CyberWarsRules.Warehouse)
            .OrderBy(e => HexCell.Distance(e.At, truck.At)).FirstOrDefault();
        if (warehouse is null)
        {
            truck.Task = HarvestTasks.Idle;
            Cue(truck.OwnerId, "Build an Ore Bonded Warehouse to unload.");
            return;
        }
        truck.Task = HarvestTasks.ToWarehouse;
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
                truck.Task = HarvestTasks.Idle;
            return;
        }
        SendToVein(truck, vein);
    }

    // ---- Geometry, power and sight ----

    bool IsStructure(MatchEntity e) => CyberWarsRules.Item(e.Kind) is { IsStructure: true };

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
        var key = factory switch
        {
            CyberWarsRules.Factories.Construction or CyberWarsRules.Factories.DroneBay => CyberWarsRules.CommandCenter,
            CyberWarsRules.Factories.Refinery => CyberWarsRules.Warehouse,
            _ => null,
        };
        return State.Entities.FirstOrDefault(e => e.OwnerId == userId && e.Kind == key);
    }

    static string FactoryBuildingName(string factory) =>
        factory == CyberWarsRules.Factories.Refinery ? "Ore Bonded Warehouse" : factory == CyberWarsRules.Factories.Construction || factory == CyberWarsRules.Factories.DroneBay ? "Command Center" : factory;

    public (int Supply, int Demand) Power(Guid userId)
    {
        int supply = 0, demand = 0;
        foreach (var e in State.Entities.Where(e => e.OwnerId == userId))
            if (CyberWarsRules.Item(e.Kind) is { } item)
                (supply, demand) = (supply + item.PowerSupply, demand + item.PowerDemand);
        return (supply, demand);
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
        return new MatchDeltaDto(State.MatchId, State.Tick, changed, removed, Math.Round(player.Credits, 2), supply, demand, cues,
            Math.Round(player.Refined, 2), queue, State.Status, State.EndReason);
    }

    IEnumerable<EntityDto> VisibleTo(Guid userId)
    {
        var sight = Sight(userId);
        return State.Entities.Where(e => e.OwnerId == userId || sight.Contains(e.At)).Select(ToDto);
    }

    static EntityDto ToDto(MatchEntity e) => new(e.Id, e.OwnerId, e.Kind, e.At, e.IsVein ? (int)Math.Ceiling(e.Ore) : e.Health, StateOf(e));

    static string? StateOf(MatchEntity e)
    {
        if (e.Kind.StartsWith("rig."))
            return $"stock {(int)e.Ore}/{CyberWarsRules.RigStockCap}";
        if (e.Kind == CyberWarsRules.Harvester)
            return e.Task switch
            {
                HarvestTasks.Loading => $"loading {(int)e.Load}/{CyberWarsRules.HarvesterCapacity}",
                HarvestTasks.ToWarehouse => $"hauling {(int)e.Load}/{CyberWarsRules.HarvesterCapacity}",
                HarvestTasks.ToVein => "to vein",
                HarvestTasks.Moving => "moving",
                _ => e.Load > 0 ? $"idle {(int)e.Load}/{CyberWarsRules.HarvesterCapacity}" : null,
            };
        return e.Path.Count > 0 ? "moving" : null;
    }

    public MatchMapDto Map => new(State.Province, State.Radius, State.Rock);

    public IReadOnlyList<HexCell> ExploredBy(Guid userId) => [.. _explored[userId]];
}
