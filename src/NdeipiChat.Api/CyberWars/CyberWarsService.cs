using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>Matches: start a skirmish (Phase 01) or a raid (Phase 02), read one, send it orders, and keep it going.</summary>
public sealed class CyberWarsService(ChatDbContext db, CyberWarsMatches matches, CyberWarsWorldService world, TimeProvider clock)
{
    /// <summary>Zambia's time zone (CAT), which decides the richness day (FR-1.3a).</summary>
    static readonly TimeSpan Cat = TimeSpan.FromHours(2);

    public async Task<CyberWarsMeDto> MeAsync(User user, CancellationToken ct)
    {
        var player = await world.PlayerAsync(user, ct);
        var home = await db.CyberWarsBases.FirstOrDefaultAsync(b => b.UserId == user.Id, ct);
        var active = await ActiveAsync(user.Id, ct);
        var now = clock.GetUtcNow();
        return new CyberWarsMeDto(user.Id, player.Rank, player.GnTokens, player.HomeProvince, active.FirstOrDefault()?.MatchId,
            home is null ? null : new HexCell(home.SectorQ, home.SectorR), await world.AllianceOfAsync(user.Id, ct),
            player.ProtectedUntil > now ? player.ProtectedUntil : null, active);
    }

    /// <summary>Every running match the player is in, newest first, with who they're up against.</summary>
    async Task<List<ActiveMatchDto>> ActiveAsync(Guid userId, CancellationToken ct)
    {
        var rows = await db.CyberWarsMatchPlayers.Where(p => p.UserId == userId)
            .Join(db.CyberWarsMatches.Where(m => m.Status == MatchStatuses.Running), p => p.MatchId, m => m.Id, (p, m) => new { m.Id, m.Mode, p.Side, m.Province, m.SectorQ, m.SectorR, m.StartedAt })
            .OrderByDescending(m => m.StartedAt).ToListAsync(ct);
        var ids = rows.Select(r => r.Id).ToList();
        var opponents = await db.CyberWarsMatchPlayers.Where(p => ids.Contains(p.MatchId) && p.UserId != userId)
            .Join(db.Users, p => p.UserId, u => u.Id, (p, u) => new { p.MatchId, u.DisplayName }).ToListAsync(ct);
        return rows.Select(r => new ActiveMatchDto(r.Id, r.Mode, r.Side, r.Province, r.SectorQ is { } q && r.SectorR is { } rr ? new HexCell(q, rr) : null,
            opponents.FirstOrDefault(o => o.MatchId == r.Id)?.DisplayName)).ToList();
    }

    /// <summary>Starts a new skirmish (FR-1.1d: no pack needed), ending any the player left running.</summary>
    public async Task<MatchStateDto> StartAsync(User user, StartMatchRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Province))
            throw new ChatRejectedException("Pick one of Zambia's ten provinces.");
        var player = await world.PlayerAsync(user, ct);
        foreach (var running in (await ActiveAsync(user.Id, ct)).Where(m => m.Mode == MatchModes.Skirmish))
            await EndAsync(user, running.MatchId, ct);

        var now = clock.GetUtcNow();
        var match = new CyberWarsMatch
        {
            Id = Guid.NewGuid(), Province = request.Province, Day = DateOnly.FromDateTime(now.ToOffset(Cat).DateTime), StartedAt = now
        };
        var engine = MatchEngine.NewSkirmish(match.Id, match.Province, match.Day, user.Id, player.Rank, user.DisplayName);
        db.CyberWarsMatches.Add(match);
        db.CyberWarsMatchPlayers.Add(new CyberWarsMatchPlayer { MatchId = match.Id, UserId = user.Id });
        db.CyberWarsSnapshots.Add(new CyberWarsSnapshot { MatchId = match.Id, Tick = 0, StateBlob = ContractJson.Write(engine.State), At = now });
        player.HomeProvince ??= request.Province;
        await db.SaveChangesAsync(ct);

        var live = matches.Add(engine);
        lock (live.Gate)
            return StateOf(live, user.Id, match.StartedAt);
    }

    /// <summary>Raids a rival's base (FR-2.3); the defender's base defends itself until they join.</summary>
    public async Task<MatchStateDto> RaidAsync(User user, StartRaidRequest request, CancellationToken ct)
    {
        var (matchId, _, startedAt) = await world.StartRaidAsync(user, request, ct);
        var live = await matches.GetAsync(matchId, db, ct) ?? throw new ChatRejectedException("The raid couldn't start. Try again.");
        lock (live.Gate)
            return StateOf(live, user.Id, startedAt);
    }

    public async Task<MatchStateDto?> StateAsync(User user, Guid matchId, CancellationToken ct)
    {
        var started = await db.CyberWarsMatchPlayers.Where(p => p.MatchId == matchId && p.UserId == user.Id)
            .Join(db.CyberWarsMatches, p => p.MatchId, m => m.Id, (_, m) => (DateTimeOffset?)m.StartedAt).FirstOrDefaultAsync(ct);
        if (started is null || await matches.GetAsync(matchId, db, ct) is not { } live)
            return null;
        lock (live.Gate)
        {
            if (live.Engine.Player(user.Id) is null)
                return null;
            live.Engine.Touch(user.Id);
            return StateOf(live, user.Id, started.Value);
        }
    }

    static MatchStateDto StateOf(LiveMatch live, Guid userId, DateTimeOffset startedAt)
    {
        var engine = live.Engine;
        return new MatchStateDto(engine.State.MatchId, CyberWarsContract.PlayerTopic(engine.State.MatchId, userId), engine.Player(userId)!.Rank,
            engine.Map, engine.CurrentFor(userId), engine.ExploredBy(userId), startedAt, engine.State.Mode, engine.Roster, engine.State.Sector);
    }

    /// <summary>
    /// Answers each order within the tick (FR-2.3) and logs every one (NFR-2a). An order id already
    /// logged gets its first answer back and isn't applied again, even after a restart.
    /// </summary>
    public async Task<IReadOnlyList<OrderResultDto>?> OrdersAsync(User user, Guid matchId, OrderBatchRequest request, CancellationToken ct)
    {
        if (request.Orders is not { Count: > 0 } orders)
            throw new ChatRejectedException("Send at least one order.");
        if (orders.Count > CyberWarsContract.MaxOrdersPerBatch)
            throw new ChatRejectedException($"Send at most {CyberWarsContract.MaxOrdersPerBatch} orders at once.");
        if (await matches.GetAsync(matchId, db, ct) is not { } live)
            return null;

        var ids = orders.Select(o => o.OrderId).ToList();
        var logged = await db.CyberWarsOrderLogs.AsNoTracking().Where(o => ids.Contains(o.OrderId)).ToDictionaryAsync(o => o.OrderId, ct);
        if (logged.Values.Any(o => o.UserId != user.Id || o.MatchId != matchId))
            throw new ChatRejectedException("Those order ids are already used.");
        var fresh = orders.Where(o => !logged.ContainsKey(o.OrderId)).DistinctBy(o => o.OrderId).ToList();

        IReadOnlyList<OrderResultDto> answers;
        var now = clock.GetUtcNow();
        lock (live.Gate)
        {
            if (live.Engine.Player(user.Id) is null)
                return null;
            live.Engine.Touch(user.Id);
            answers = fresh.Count == 0 ? [] : live.Engine.Submit(user.Id, fresh, now);
        }

        var byId = answers.ToDictionary(a => a.OrderId);
        foreach (var order in fresh)
        {
            var answer = byId[order.OrderId];
            db.CyberWarsOrderLogs.Add(new CyberWarsOrderLog
            {
                OrderId = order.OrderId, MatchId = matchId, UserId = user.Id, Kind = order.Kind, Tick = answer.Tick,
                Accepted = answer.Accepted, Reason = answer.Reason, At = now
            });
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same order sent twice at once: the engine applied it once, and the first log stands.
        }
        return orders.Select(o => logged.TryGetValue(o.OrderId, out var l) ? new OrderResultDto(l.OrderId, l.Accepted, l.Tick, l.Reason) : byId[o.OrderId]).ToList();
    }

    /// <summary>Keeps the match running while the player has it open, and the AI out of their seat (FR-2.4).</summary>
    public async Task<bool> PresenceAsync(User user, Guid matchId, CancellationToken ct)
    {
        if (!await IsInAsync(user, matchId, ct) || await matches.GetAsync(matchId, db, ct) is not { } live)
            return false;
        lock (live.Gate)
            live.Engine.Touch(user.Id);
        return true;
    }

    /// <summary>Leaves a skirmish, or gives up a raid: the other side wins it.</summary>
    public async Task<bool> EndAsync(User user, Guid matchId, CancellationToken ct)
    {
        if (!await IsInAsync(user, matchId, ct) || await matches.GetAsync(matchId, db, ct) is not { } live)
            return false;
        lock (live.Gate)
        {
            var engine = live.Engine;
            if (!engine.IsOver)
            {
                var other = engine.State.Players.FirstOrDefault(p => p.UserId != user.Id);
                if (engine.IsRaid && other is not null)
                    engine.End(MatchStatuses.Won, $"{user.DisplayName} gave up the raid.", other.UserId);
                else
                    engine.End(MatchStatuses.Ended, "You left the skirmish.");
            }
        }
        // The loop sends the last delta, saves it and settles the match; until then, don't offer it as resumable.
        await db.CyberWarsMatches.Where(m => m.Id == matchId && m.Status == MatchStatuses.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MatchStatuses.Ended), ct);
        return true;
    }

    Task<bool> IsInAsync(User user, Guid matchId, CancellationToken ct) =>
        db.CyberWarsMatchPlayers.AnyAsync(p => p.MatchId == matchId && p.UserId == user.Id, ct);
}

/// <summary>
/// "cyberwars:{matchId}:{userId}" is only for that user, and only while they're in that match;
/// "cyberwars:map:{province}" is for anyone who may use CyberWars.
/// </summary>
public sealed class CyberWarsTopicPolicy(ChatDbContext db, Launcher.LauncherService launcher) : ITopicPolicy
{
    public string Prefix => CyberWarsContract.AppId;

    public async Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct)
    {
        if (!launcher.CanUse(user, CyberWarsContract.AppId))
            return false;
        var parts = key.Split(':');
        if (parts is ["map", var province])
            return Enum.TryParse<ZambianProvince>(province, out var p) && Enum.IsDefined(p) && p.ToString() == province;
        return parts is [var match, var userId]
            && Guid.TryParseExact(match, "N", out var matchId) && Guid.TryParseExact(userId, "N", out var id) && id == user.Id
            && await db.CyberWarsMatchPlayers.AnyAsync(p => p.MatchId == matchId && p.UserId == user.Id, ct);
    }
}
