using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>A match held in memory, with the lock every touch of its engine goes through.</summary>
public sealed class LiveMatch(MatchEngine engine, DateTimeOffset now)
{
    public MatchEngine Engine { get; } = engine;
    public Lock Gate { get; } = new();
    public DateTimeOffset LastSeen { get; set; } = now;
    public long SavedTick { get; set; } = engine.State.Tick;
}

/// <summary>
/// The matches running on this instance. <see cref="TickAsync"/> steps each one every 100 ms,
/// pushes each player their delta, snapshots every 10 s (NFR-3a), and puts a match away, saved,
/// once nobody has looked at it for <see cref="IdleAfter"/>. A match not in memory (after a restart,
/// or put away) is loaded back from its snapshot the next time its player asks for it.
/// </summary>
public sealed class CyberWarsMatches(IServiceScopeFactory scopes, ChatNotifier notifier, TimeProvider clock, ILogger<CyberWarsMatches> log)
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromMinutes(2);
    public const int SnapshotEveryTicks = 10 * CyberWarsContract.TicksPerSecond;

    readonly ConcurrentDictionary<Guid, LiveMatch> _live = new();
    readonly SemaphoreSlim _loading = new(1, 1);

    public LiveMatch Add(MatchEngine engine) => _live[engine.State.MatchId] = new LiveMatch(engine, clock.GetUtcNow());

    public bool IsLive(Guid matchId) => _live.ContainsKey(matchId);

    /// <summary>Forgets a match without saving it, as a server restart would.</summary>
    public void Drop(Guid matchId) => _live.TryRemove(matchId, out _);

    public async Task<LiveMatch?> GetAsync(Guid matchId, ChatDbContext db, CancellationToken ct)
    {
        if (_live.TryGetValue(matchId, out var live))
            return Touch(live);
        await _loading.WaitAsync(ct);
        try
        {
            if (_live.TryGetValue(matchId, out live))
                return Touch(live);
            var snapshot = await db.CyberWarsSnapshots.AsNoTracking().FirstOrDefaultAsync(s => s.MatchId == matchId, ct);
            if (snapshot is null || ContractJson.Read<MatchState>(snapshot.StateBlob) is not { } state)
                return null;
            return Add(new MatchEngine(state));
        }
        finally
        {
            _loading.Release();
        }
    }

    LiveMatch Touch(LiveMatch live)
    {
        live.LastSeen = clock.GetUtcNow();
        return live;
    }

    /// <summary>One tick for every match in memory.</summary>
    public async Task TickAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        foreach (var (id, live) in _live)
        {
            var deltas = new List<(string Topic, MatchDeltaDto Delta)>();
            string? blob = null;
            long tick;
            bool over, idle;
            string status;
            lock (live.Gate)
            {
                var engine = live.Engine;
                idle = now - live.LastSeen > IdleAfter;
                if (!idle)
                {
                    engine.Step();
                    foreach (var player in engine.State.Players)
                        deltas.Add((CyberWarsContract.PlayerTopic(id, player.UserId), engine.DeltaFor(player.UserId)));
                }
                over = engine.IsOver;
                tick = engine.State.Tick;
                status = engine.State.Status;
                if (idle || over || tick - live.SavedTick >= SnapshotEveryTicks)
                {
                    blob = ContractJson.Write(engine.State);
                    live.SavedTick = tick;
                }
            }

            try
            {
                foreach (var (topic, delta) in deltas)
                    await notifier.PublishAsync(topic, delta);
                if (blob is not null)
                    await SaveAsync(id, tick, blob, over ? status : null, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                log.LogError(ex, "CyberWars match {MatchId} failed to publish or save tick {Tick}", id, tick);
            }

            if (idle || over)
                _live.TryRemove(id, out _);
        }
    }

    async Task SaveAsync(Guid matchId, long tick, string blob, string? finalStatus, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var now = clock.GetUtcNow();
        var updated = await db.CyberWarsSnapshots.Where(s => s.MatchId == matchId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Tick, tick).SetProperty(x => x.StateBlob, blob).SetProperty(x => x.At, now), ct);
        if (updated == 0)
        {
            db.CyberWarsSnapshots.Add(new CyberWarsSnapshot { MatchId = matchId, Tick = tick, StateBlob = blob, At = now });
            await db.SaveChangesAsync(ct);
        }
        if (finalStatus is not null)
            await db.CyberWarsMatches.Where(m => m.Id == matchId && m.EndedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, finalStatus).SetProperty(m => m.EndedAt, now), ct);
    }
}

/// <summary>The authoritative 10 Hz loop (NFR-1).</summary>
public sealed class CyberWarsLoop(CyberWarsMatches matches, TimeProvider clock, ILogger<CyberWarsLoop> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(CyberWarsContract.TickMilliseconds), clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await matches.TickAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "CyberWars tick failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }
}
