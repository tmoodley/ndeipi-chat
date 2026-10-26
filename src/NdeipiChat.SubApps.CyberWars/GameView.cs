using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.CyberWars;

/// <summary>
/// The client's picture of a match: the last state the server sent plus every delta since. It only
/// draws; the server decides everything (NFR-2). Sight and explored ground are worked out here from
/// the player's own units, the same way the server does, to draw the fog (UI-2a).
/// </summary>
public sealed class GameView
{
    public GameView(MatchStateDto start, Guid me)
    {
        Me = me;
        Start = start;
        Map = start.Map;
        Rock = [.. start.Map.Rock];
        Cells = [.. new HexCell(0, 0).Within(start.Map.Radius)];
        Explored = [.. start.Explored];
        Entities = start.State.Changed.ToDictionary(e => e.Id);
        Last = start.State;
        Refresh();
    }

    public Guid Me { get; }
    public MatchStateDto Start { get; }
    public MatchMapDto Map { get; }
    public HashSet<HexCell> Rock { get; }
    public HashSet<HexCell> Cells { get; }
    public HashSet<HexCell> Explored { get; }
    public HashSet<HexCell> Visible { get; private set; } = [];
    public Dictionary<Guid, EntityDto> Entities { get; }

    /// <summary>Veins seen and then left: drawn dimmed where they were last seen (UI-2a).</summary>
    public Dictionary<Guid, EntityDto> Ghosts { get; } = [];

    public MatchDeltaDto Last { get; private set; }
    public long Tick => Last.Tick;

    /// <summary>Bumped whenever the fog changes, so the ground is only redrawn then.</summary>
    public int FogVersion { get; private set; }

    public bool IsOver => Last.Status != MatchStatuses.Running;

    /// <summary>
    /// Applies the next delta. False when one was missed (a reconnect, say): the caller then asks for
    /// the whole state again, since a delta only makes sense on top of the one before it.
    /// </summary>
    public bool Apply(MatchDeltaDto delta)
    {
        if (delta.Tick <= Last.Tick)
            return true;
        if (delta.Tick != Last.Tick + 1)
            return false;
        foreach (var e in delta.Changed)
        {
            Entities[e.Id] = e;
            Ghosts.Remove(e.Id);
        }
        // Out of sight, buildings and veins stay where they were last seen, dimmed; units just vanish (UI-2a).
        foreach (var id in delta.Removed)
            if (Entities.Remove(id, out var gone) && gone.OwnerId != Me
                && (gone.Kind.StartsWith("vein.") || CyberWarsRules.Item(gone.Kind) is { IsStructure: true }))
                Ghosts[id] = gone;
        Last = delta;
        Refresh();
        return true;
    }

    void Refresh()
    {
        var sight = new HashSet<HexCell>();
        foreach (var e in Entities.Values.Where(e => e.OwnerId == Me))
            if (CyberWarsRules.Item(e.Kind) is { Sight: > 0 } item)
                sight.UnionWith(e.At.Within(item.Sight));
        sight.IntersectWith(Cells);
        // A ghost whose ground is in sight again, and not sent, is gone for real.
        foreach (var (id, ghost) in Ghosts.ToList())
            if (sight.Contains(ghost.At))
                Ghosts.Remove(id);
        var before = Explored.Count;
        Explored.UnionWith(sight);
        if (before != Explored.Count || !sight.SetEquals(Visible))
            FogVersion++;
        Visible = sight;
    }

    public IEnumerable<EntityDto> Mine => Entities.Values.Where(e => e.OwnerId == Me);

    public HashSet<string> Owned => [.. Mine.Select(e => e.Kind)];

    public EntityDto? VeinAt(HexCell cell) => Entities.Values.FirstOrDefault(e => e.Kind.StartsWith("vein.") && e.At == cell);

    public (int Supply, int Demand) Power => (Last.PowerSupply, Last.PowerDemand);

    /// <summary>Where a structure may go, mirroring the server's check, to light up the ground.</summary>
    public HashSet<HexCell> PlacementCells(string key)
    {
        var rig = CyberWarsRules.RigRarityOf(key) is not null;
        var structures = Mine.Where(e => CyberWarsRules.Item(e.Kind) is { IsStructure: true }).ToList();
        var taken = Entities.Values.Where(e => CyberWarsRules.Item(e.Kind) is { IsStructure: true }).Select(e => e.At).ToHashSet();
        var veins = Entities.Values.Concat(Ghosts.Values).Where(e => e.Kind.StartsWith("vein.")).Select(e => e.At).ToHashSet();
        return Cells.Where(c => Explored.Contains(c) && !Rock.Contains(c) && !taken.Contains(c) && rig == veins.Contains(c)
            && structures.Any(s => HexCell.Distance(s.At, c) <= CyberWarsRules.BuildRadius)).ToHashSet();
    }
}

/// <summary>Pointy-top hexes, squashed vertically for the 2.5D look.</summary>
public sealed class HexLayout(int radius)
{
    public const double Size = 30;
    public const double Squash = 0.72;
    public static readonly double Width = Math.Sqrt(3) * Size;

    public int Radius { get; } = radius;
    public double OffsetX => Width * Radius + Width / 2;
    public double OffsetY => 1.5 * Size * Squash * Radius + Size * Squash + 24;
    public double PixelWidth => Width * (2 * Radius + 1);
    public double PixelHeight => OffsetY * 2;

    public (double X, double Y) Centre(HexCell c) => (OffsetX + Width * (c.Q + c.R / 2.0), OffsetY + 1.5 * Size * Squash * c.R);

    public HexCell CellAt(double x, double y)
    {
        var r = (y - OffsetY) / (1.5 * Size * Squash);
        var q = (x - OffsetX) / Width - r / 2;
        var s = -q - r;
        double rq = Math.Round(q), rr = Math.Round(r), rs = Math.Round(s);
        double dq = Math.Abs(rq - q), dr = Math.Abs(rr - r), ds = Math.Abs(rs - s);
        if (dq > dr && dq > ds)
            rq = -rr - rs;
        else if (dr > ds)
            rr = -rq - rs;
        return new HexCell((int)rq, (int)rr);
    }

    public string Points(HexCell c)
    {
        var (x, y) = Centre(c);
        return string.Join(' ', Enumerable.Range(0, 6).Select(i =>
        {
            var a = Math.PI / 180 * (60 * i - 30);
            return FormattableString.Invariant($"{x + Size * Math.Cos(a):0.#},{y + Size * Squash * Math.Sin(a):0.#}");
        }));
    }
}
