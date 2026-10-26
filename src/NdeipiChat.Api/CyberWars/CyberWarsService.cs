using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>Solo skirmishes (Phase 01): start or resume one, read it, and send it orders.</summary>
public sealed class CyberWarsService(ChatDbContext db, CyberWarsMatches matches, TimeProvider clock)
{
    /// <summary>Zambia's time zone (CAT), which decides the richness day (FR-1.3a).</summary>
    static readonly TimeSpan Cat = TimeSpan.FromHours(2);

    public async Task<CyberWarsMeDto> MeAsync(User user, CancellationToken ct)
    {
        var player = await PlayerAsync(user, ct);
        return new CyberWarsMeDto(user.Id, player.Rank, player.GnTokens, player.HomeProvince, await ActiveMatchIdAsync(user, ct));
    }

    /// <summary>Starts a new skirmish (FR-1.1d: no pack needed), ending any the player left running.</summary>
    public async Task<MatchStateDto> StartAsync(User user, StartMatchRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Province))
            throw new ChatRejectedException("Pick one of Zambia's ten provinces.");
        var player = await PlayerAsync(user, ct);
        if (await ActiveMatchIdAsync(user, ct) is { } running)
            await EndAsync(user, running, ct);

        var now = clock.GetUtcNow();
        var match = new CyberWarsMatch
        {
            Id = Guid.NewGuid(), Province = request.Province, Day = DateOnly.FromDateTime(now.ToOffset(Cat).DateTime), StartedAt = now
        };
        var engine = MatchEngine.NewSkirmish(match.Id, match.Province, match.Day, user.Id, player.Rank);
        db.CyberWarsMatches.Add(match);
        db.CyberWarsMatchPlayers.Add(new CyberWarsMatchPlayer { MatchId = match.Id, UserId = user.Id });
        db.CyberWarsSnapshots.Add(new CyberWarsSnapshot { MatchId = match.Id, Tick = 0, StateBlob = ContractJson.Write(engine.State), At = now });
        player.HomeProvince ??= request.Province;
        await db.SaveChangesAsync(ct);

        var live = matches.Add(engine);
        lock (live.Gate)
            return StateOf(live, user.Id, match.StartedAt);
    }

    public async Task<MatchStateDto?> StateAsync(User user, Guid matchId, CancellationToken ct)
    {
        var started = await db.CyberWarsMatchPlayers.Where(p => p.MatchId == matchId && p.UserId == user.Id)
            .Join(db.CyberWarsMatches, p => p.MatchId, m => m.Id, (_, m) => (DateTimeOffset?)m.StartedAt).FirstOrDefaultAsync(ct);
        if (started is null || await matches.GetAsync(matchId, db, ct) is not { } live)
            return null;
        lock (live.Gate)
            return live.Engine.Player(user.Id) is null ? null : StateOf(live, user.Id, started.Value);
    }

    static MatchStateDto StateOf(LiveMatch live, Guid userId, DateTimeOffset startedAt)
    {
        var engine = live.Engine;
        return new MatchStateDto(engine.State.MatchId, CyberWarsContract.PlayerTopic(engine.State.MatchId, userId), engine.Player(userId)!.Rank,
            engine.Map, engine.CurrentFor(userId), engine.ExploredBy(userId), startedAt);
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

    /// <summary>Keeps the match running while the player has it open.</summary>
    public async Task<bool> PresenceAsync(User user, Guid matchId, CancellationToken ct) =>
        await IsInAsync(user, matchId, ct) && await matches.GetAsync(matchId, db, ct) is not null;

    public async Task<bool> EndAsync(User user, Guid matchId, CancellationToken ct)
    {
        if (!await IsInAsync(user, matchId, ct) || await matches.GetAsync(matchId, db, ct) is not { } live)
            return false;
        lock (live.Gate)
        {
            if (!live.Engine.IsOver)
                live.Engine.End(MatchStatuses.Ended, "You left the skirmish.");
        }
        // The loop sends the last delta and saves it; until then, don't offer it as resumable.
        await db.CyberWarsMatches.Where(m => m.Id == matchId && m.EndedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, MatchStatuses.Ended).SetProperty(m => m.EndedAt, clock.GetUtcNow()), ct);
        return true;
    }

    Task<bool> IsInAsync(User user, Guid matchId, CancellationToken ct) =>
        db.CyberWarsMatchPlayers.AnyAsync(p => p.MatchId == matchId && p.UserId == user.Id, ct);

    Task<Guid?> ActiveMatchIdAsync(User user, CancellationToken ct) =>
        db.CyberWarsMatchPlayers.Where(p => p.UserId == user.Id)
            .Join(db.CyberWarsMatches, p => p.MatchId, m => m.Id, (_, m) => m)
            .Where(m => m.EndedAt == null)
            .OrderByDescending(m => m.StartedAt).Select(m => (Guid?)m.Id).FirstOrDefaultAsync(ct);

    async Task<CyberWarsPlayer> PlayerAsync(User user, CancellationToken ct)
    {
        if (await db.CyberWarsPlayers.FirstOrDefaultAsync(p => p.UserId == user.Id, ct) is { } player)
            return player;
        player = new CyberWarsPlayer { UserId = user.Id, CreatedAt = clock.GetUtcNow() };
        db.CyberWarsPlayers.Add(player);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(player).State = EntityState.Detached;
            player = await db.CyberWarsPlayers.FirstAsync(p => p.UserId == user.Id, ct);
        }
        return player;
    }
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
            return Enum.TryParse<ZambianProvince>(province, out _);
        return parts is [var match, var userId]
            && Guid.TryParseExact(match, "N", out var matchId) && Guid.TryParseExact(userId, "N", out var id) && id == user.Id
            && await db.CyberWarsMatchPlayers.AnyAsync(p => p.MatchId == matchId && p.UserId == user.Id, ct);
    }
}
