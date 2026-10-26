using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.CyberWars;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// CyberWars: GN Mining, Phase 01 (solo skirmish) against SRS v1.1: the build queue's charge-on-start
/// and refunds (FR-1.2a), ore credits rising only on a harvester's unload (FR-1.3, NFR-2), refused
/// orders for another player's unit, over-speed and fogged targets (NFR-2a), fog on the wire
/// (NFR-2b), same-day richness (FR-1.3a), and the match surviving a restart from its snapshot.
/// </summary>
public sealed class CyberWarsTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = CyberWarsContract.BasePath;
    static readonly DateOnly Day = new(2026, 10, 26);
    static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    // ---- The engine ----

    static MatchEngine Skirmish(Guid me) => MatchEngine.NewSkirmish(Guid.NewGuid(), ZambianProvince.Copperbelt, Day, me, PlayerRank.None);

    static OrderResultDto Send(MatchEngine engine, Guid userId, OrderKind kind, IReadOnlyList<Guid>? units = null, HexCell? target = null,
        Guid? entity = null, string? item = null, long? clientTick = null) =>
        engine.Submit(userId, [new RtsOrder(Guid.NewGuid(), clientTick ?? engine.State.Tick, kind, units ?? [], target, entity, item)], Now).Single();

    static void RunUntil(MatchEngine engine, Func<bool> done, string because, int maxTicks = 5000)
    {
        for (var i = 0; i < maxTicks && !done(); i++)
            engine.Step();
        Assert.True(done(), $"Gave up waiting until {because}.");
    }

    static MatchEntity Mine(MatchEngine engine, Guid me, string kind) => engine.State.Entities.First(e => e.OwnerId == me && e.Kind == kind);

    /// <summary>An open, explored, unbuilt hex near the Command Center, off the veins.</summary>
    static HexCell FreeCell(MatchEngine engine, Guid me)
    {
        var home = Mine(engine, me, CyberWarsRules.CommandCenter).At;
        var explored = engine.State.Players.Single(p => p.UserId == me).Explored.ToHashSet();
        return home.Within(3).Where(c => c != home && explored.Contains(c) && !engine.State.Rock.Contains(c) && engine.State.Entities.All(e => e.At != c))
            .OrderBy(c => HexCell.Distance(c, home)).ThenBy(c => c.Q).ThenBy(c => c.R).First();
    }

    static void BuildAndPlace(MatchEngine engine, Guid me, string key, HexCell? at = null)
    {
        Assert.True(Send(engine, me, OrderKind.Build, item: key).Accepted);
        RunUntil(engine, () => engine.Player(me)!.Queue.Any(j => j.Key == key && j.Ready), $"{key} is built");
        var placed = Send(engine, me, OrderKind.Place, target: at ?? FreeCell(engine, me), item: key);
        Assert.True(placed.Accepted, placed.Reason);
        engine.Step();
        Assert.Contains(engine.State.Entities, e => e.OwnerId == me && e.Kind == key);
    }

    [Fact]
    public void Ore_credits_rise_only_when_a_harvester_unloads_at_the_warehouse()
    {
        var me = Guid.NewGuid();
        var engine = Skirmish(me);
        BuildAndPlace(engine, me, CyberWarsRules.PowerNode);
        BuildAndPlace(engine, me, CyberWarsRules.Warehouse);
        var player = engine.Player(me)!;
        Assert.Equal(CyberWarsRules.StartingCredits - 300 - 800, player.Credits);
        var warehouse = Mine(engine, me, CyberWarsRules.Warehouse);
        Assert.Single(engine.State.Entities, e => e.OwnerId == me && e.Kind == CyberWarsRules.Harvester);

        // Nothing else is building, so the balance may only move when a load is unloaded, and by
        // exactly that load's worth (copper here: the field next to home).
        var unloads = 0;
        decimal earned = 0;
        for (var tick = 0; tick < 6000 && unloads < 3; tick++)
        {
            var loads = engine.State.Entities.Where(e => e.Kind == CyberWarsRules.Harvester).ToDictionary(e => e.Id, e => (e.Load, e.LoadIsGold));
            var before = player.Credits;
            engine.Step();
            if (player.Credits == before)
                continue;
            var unloaded = engine.State.Entities.Where(e => e.Kind == CyberWarsRules.Harvester && loads[e.Id].Load > 0 && e.Load == 0).ToList();
            var truck = Assert.Single(unloaded);
            Assert.True(HexCell.Distance(truck.At, warehouse.At) <= 1, "Credits rose away from the warehouse.");
            Assert.Equal(Math.Round(loads[truck.Id].Load * CyberWarsRules.CreditsPerOre(loads[truck.Id].LoadIsGold), 2), player.Credits - before);
            earned += player.Credits - before;
            unloads++;
        }
        Assert.Equal(3, unloads);
        Assert.Equal(earned, player.Refined);
        Assert.Equal(engine.State.Tick, engine.DeltaFor(me).Tick);
        Assert.Equal(player.Credits, engine.DeltaFor(me).OreCredits);
    }

    [Fact]
    public void A_queued_item_is_charged_when_it_starts_and_refunded_only_if_it_had()
    {
        var me = Guid.NewGuid();
        var engine = Skirmish(me);
        var player = engine.Player(me)!;

        Assert.True(Send(engine, me, OrderKind.Build, item: CyberWarsRules.PowerNode).Accepted);
        Assert.True(Send(engine, me, OrderKind.Build, item: CyberWarsRules.RigKey(RigRarity.Common)).Accepted);
        Assert.Equal(CyberWarsRules.StartingCredits, player.Credits); // Queued, not charged.
        engine.Step();
        engine.Step();
        Assert.Equal(CyberWarsRules.StartingCredits - 300, player.Credits); // One per factory: only the power node started.
        var (power, rig) = (player.Queue[0], player.Queue[1]);
        Assert.True(power.Started);
        Assert.False(rig.Started);

        Assert.True(Send(engine, me, OrderKind.Cancel, entity: rig.Id).Accepted);
        engine.Step();
        Assert.Equal(CyberWarsRules.StartingCredits - 300, player.Credits); // Nothing was charged, nothing refunded.

        Assert.True(Send(engine, me, OrderKind.Cancel, entity: power.Id).Accepted);
        engine.Step();
        Assert.Equal(CyberWarsRules.StartingCredits, player.Credits); // Full refund.
        Assert.Empty(player.Queue);
    }

    [Fact]
    public void Without_a_pack_a_player_gets_one_rig_and_locked_items_say_what_unlocks_them()
    {
        var me = Guid.NewGuid();
        var engine = Skirmish(me);
        Assert.True(Send(engine, me, OrderKind.Build, item: CyberWarsRules.RigKey(RigRarity.Common)).Accepted);
        engine.Step();
        Assert.Contains("1 rig slot", Send(engine, me, OrderKind.Build, item: CyberWarsRules.RigKey(RigRarity.Common)).Reason);
        Assert.Equal("Unlocks with the Commander pack", Send(engine, me, OrderKind.Build, item: CyberWarsRules.RigKey(RigRarity.Rare)).Reason);
        Assert.Equal("Unlocked by building the Ore Bonded Warehouse", Send(engine, me, OrderKind.Build, item: CyberWarsRules.Harvester).Reason);
        Assert.Equal(4, CyberWarsRules.Rigs.Select(r => r.OrePerSecond).Distinct().Count());
        Assert.True(CyberWarsRules.Rigs.Zip(CyberWarsRules.Rigs.Skip(1)).All(p => p.First.OrePerSecond < p.Second.OrePerSecond && p.First.Armour < p.Second.Armour));
    }

    /// <summary>A skirmish with a harvester of mine at home and a rival's drone out in my fog.</summary>
    static (MatchEngine Engine, Guid Me, Guid Rival, MatchEntity Harvester, MatchEntity RivalDrone) Contested()
    {
        var (me, rival) = (Guid.NewGuid(), Guid.NewGuid());
        var state = Skirmish(me).State;
        var home = state.Entities.Single(e => e.Kind == CyberWarsRules.CommandCenter).At;
        var harvester = new MatchEntity { Id = Guid.NewGuid(), OwnerId = me, Kind = CyberWarsRules.Harvester, At = new HexCell(home.Q, home.R + 1), Health = 300 };
        var drone = new MatchEntity { Id = Guid.NewGuid(), OwnerId = rival, Kind = CyberWarsRules.Drone, At = new HexCell(9, -3), Health = 60 };
        state.Entities.AddRange([harvester, drone]);
        state.Players.Add(new MatchPlayer { UserId = rival, Credits = CyberWarsRules.StartingCredits });
        return (new MatchEngine(state), me, rival, harvester, drone);
    }

    [Fact]
    public void Orders_for_another_players_unit_over_speed_or_into_fog_are_refused()
    {
        var (engine, me, _, harvester, rivalDrone) = Contested();
        var myDrone = Mine(engine, me, CyberWarsRules.Drone);
        var somewhere = new HexCell(0, 0);

        // Another player's unit.
        var stolen = Send(engine, me, OrderKind.Move, [rivalDrone.Id], somewhere);
        Assert.False(stolen.Accepted);
        Assert.Equal("That unit isn't yours.", stolen.Reason);

        // Over-speed: an order stamped with a tick the server hasn't reached, and a second order for
        // the same unit inside one tick.
        var early = Send(engine, me, OrderKind.Move, [myDrone.Id], somewhere, clientTick: engine.State.Tick + 5);
        Assert.False(early.Accepted);
        Assert.Contains("too fast", early.Reason);
        Assert.True(Send(engine, me, OrderKind.Move, [myDrone.Id], somewhere).Accepted);
        var again = Send(engine, me, OrderKind.Move, [myDrone.Id], new HexCell(1, 0));
        Assert.False(again.Accepted);
        Assert.Contains("Too fast", again.Reason);

        // A vein the player has never seen.
        var explored = engine.State.Players.Single(p => p.UserId == me).Explored.ToHashSet();
        var hidden = engine.State.Entities.First(e => e.IsVein && !explored.Contains(e.At));
        var fogged = Send(engine, me, OrderKind.Harvest, [harvester.Id], entity: hidden.Id);
        Assert.False(fogged.Accepted);
        Assert.Equal("You can't see there.", fogged.Reason);
        Assert.Equal("You can't see there.", Send(engine, me, OrderKind.Harvest, [harvester.Id], hidden.At).Reason);
        Assert.Equal("You can't see that target.", Send(engine, me, OrderKind.Attack, [harvester.Id], entity: rivalDrone.Id).Reason);

        // A seen vein is fine, and refused orders changed nothing.
        var near = engine.State.Entities.First(e => e.IsVein && explored.Contains(e.At));
        Assert.True(Send(engine, me, OrderKind.Harvest, [harvester.Id], entity: near.Id).Accepted);
        engine.Step();
        Assert.Equal(new HexCell(9, -3), rivalDrone.At);
        Assert.Empty(rivalDrone.Path);
    }

    [Fact]
    public void Twenty_refused_orders_in_a_minute_end_the_match()
    {
        var (engine, me, _, _, rivalDrone) = Contested();
        for (var i = 0; i < CyberWarsRules.RefusalLimit - 1; i++)
            Assert.False(Send(engine, me, OrderKind.Move, [rivalDrone.Id], new HexCell(0, 0)).Accepted);
        Assert.False(engine.IsOver);
        Send(engine, me, OrderKind.Move, [rivalDrone.Id], new HexCell(0, 0));
        Assert.True(engine.IsOver);
        Assert.Contains("refused orders", engine.State.EndReason);
        Assert.Equal("This match is over.", Send(engine, me, OrderKind.Stop, [Mine(engine, me, CyberWarsRules.Drone).Id]).Reason);
    }

    [Fact]
    public void A_player_is_sent_only_what_their_units_see()
    {
        var (engine, me, rival, _, rivalDrone) = Contested();
        engine.Step();
        var sight = engine.Sight(me);
        var delta = engine.DeltaFor(me);
        Assert.All(delta.Changed, e => Assert.True(e.OwnerId == me || sight.Contains(e.At), $"{e.Kind} at {e.At} is outside sight"));
        Assert.DoesNotContain(delta.Changed, e => e.Id == rivalDrone.Id);
        Assert.Contains(delta.Changed, e => e.Kind.StartsWith("vein."));
        Assert.True(engine.State.Entities.Count(e => e.IsVein) > delta.Changed.Count(e => e.Kind.StartsWith("vein.")), "Every vein was sent.");
        Assert.DoesNotContain(engine.DeltaFor(rival).Changed, e => e.OwnerId == me);
    }

    [Fact]
    public void Two_skirmishes_on_the_same_day_in_the_same_province_have_the_same_veins()
    {
        static IEnumerable<(HexCell, string, decimal)> Veins(MatchEngine e) => e.State.Entities.Where(v => v.IsVein).Select(v => (v.At, v.Kind, v.Ore));

        var (a, b) = (Skirmish(Guid.NewGuid()), Skirmish(Guid.NewGuid()));
        Assert.Equal(Veins(a), Veins(b));
        Assert.Equal(a.State.Rock, b.State.Rock);
        var elsewhere = MatchEngine.NewSkirmish(Guid.NewGuid(), ZambianProvince.Eastern, Day, Guid.NewGuid(), PlayerRank.None);
        Assert.NotEqual(Veins(a), Veins(elsewhere));
    }

    // ---- The API ----

    async Task<MatchStateDto> StartAsync(TestUser user) =>
        await user.PostAsync<MatchStateDto>($"{Base}/matches", new StartMatchRequest(ZambianProvince.Lusaka));

    static RtsOrder Move(MatchStateDto match, Guid unit, HexCell to, long? tick = null) =>
        new(Guid.NewGuid(), tick ?? 0, OrderKind.Move, [unit], to, null, null);

    [Fact]
    public async Task A_player_without_a_pack_starts_a_skirmish_and_orders_are_answered_and_logged()
    {
        var player = await app.CreateUserAsync("CyberWars Explorer");
        var me = await player.GetAsync<CyberWarsMeDto>($"{Base}/me");
        Assert.Equal((PlayerRank.None, (Guid?)null), (me.Rank, me.ActiveMatchId));

        var match = await StartAsync(player);
        Assert.Equal(CyberWarsContract.PlayerTopic(match.MatchId, player.Id), match.Topic);
        Assert.Equal(CyberWarsRules.StartingCredits, match.State.OreCredits);
        Assert.Contains(match.State.Changed, e => e.Kind == CyberWarsRules.CommandCenter && e.OwnerId == player.Id);
        Assert.Equal(match.MatchId, (await player.GetAsync<CyberWarsMeDto>($"{Base}/me")).ActiveMatchId);
        var drone = match.State.Changed.Single(e => e.Kind == CyberWarsRules.Drone);

        // An order from the future is refused, logged with its reason, and answered the same way when retried.
        var early = Move(match, drone.Id, new HexCell(0, 0), tick: 1_000_000);
        var first = (await player.PostAsync<List<OrderResultDto>>($"{Base}/matches/{match.MatchId}/orders", new OrderBatchRequest([early]))).Single();
        Assert.False(first.Accepted);
        var retried = (await player.PostAsync<List<OrderResultDto>>($"{Base}/matches/{match.MatchId}/orders", new OrderBatchRequest([early]))).Single();
        Assert.Equal(first, retried);
        var log = await app.DbAsync(db => db.CyberWarsOrderLogs.Where(o => o.MatchId == match.MatchId).ToListAsync());
        var entry = Assert.Single(log);
        Assert.Equal((early.OrderId, player.Id, false, first.Reason), (entry.OrderId, entry.UserId, entry.Accepted, entry.Reason));

        var ok = (await player.PostAsync<List<OrderResultDto>>($"{Base}/matches/{match.MatchId}/orders",
            new OrderBatchRequest([Move(match, drone.Id, new HexCell(-2, 2))]))).Single();
        Assert.True(ok.Accepted, ok.Reason);

        // Someone else can't order this match about, or read it.
        var stranger = await app.CreateUserAsync("CyberWars Stranger");
        using var theirs = await stranger.Http.PostAsJsonAsync($"{Base}/matches/{match.MatchId}/orders",
            new OrderBatchRequest([Move(match, drone.Id, new HexCell(0, 0))]), ContractJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, theirs.StatusCode);
        using var peek = await stranger.Http.GetAsync($"{Base}/matches/{match.MatchId}");
        Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);

        // Ending it leaves nothing to resume.
        (await player.Http.PostAsync($"{Base}/matches/{match.MatchId}/end", null)).EnsureSuccessStatusCode();
        Assert.Null((await player.GetAsync<CyberWarsMeDto>($"{Base}/me")).ActiveMatchId);
    }

    [Fact]
    public async Task Deltas_arrive_on_the_players_own_topic_and_nobody_else_may_follow_it()
    {
        var player = await app.CreateUserAsync("CyberWars Live");
        var match = await StartAsync(player);
        await using var hub = await app.ConnectAsync(player);
        var delta = Wait.ForEventAsync<TopicMessageDto>(hub, nameof(IChatClient.TopicMessage), m => m.Topic == match.Topic);
        await hub.InvokeAsync(ChatHubContract.Subscribe, match.Topic);
        var received = ContractJson.Read<MatchDeltaDto>((await delta).Payload)!;
        Assert.Equal(match.MatchId, received.MatchId);
        Assert.True(received.Tick > match.State.Tick);

        var spy = await app.CreateUserAsync("CyberWars Spy");
        await using var spyHub = await app.ConnectAsync(spy);
        await Assert.ThrowsAsync<HubException>(() => spyHub.InvokeAsync(ChatHubContract.Subscribe, match.Topic));
        await Assert.ThrowsAsync<HubException>(() => spyHub.InvokeAsync(ChatHubContract.Subscribe, CyberWarsContract.PlayerTopic(match.MatchId, spy.Id)));
        await spyHub.InvokeAsync(ChatHubContract.Subscribe, CyberWarsContract.MapTopic(ZambianProvince.Lusaka));
    }

    [Fact]
    public async Task A_match_resumes_from_its_snapshot_after_a_restart()
    {
        var player = await app.CreateUserAsync("CyberWars Restart");
        var match = await StartAsync(player);
        var drone = match.State.Changed.Single(e => e.Kind == CyberWarsRules.Drone);
        var matches = app.Services.GetRequiredService<CyberWarsMatches>();

        matches.Drop(match.MatchId);
        Assert.False(matches.IsLive(match.MatchId));

        var resumed = await player.GetAsync<MatchStateDto>($"{Base}/matches/{match.MatchId}");
        Assert.True(matches.IsLive(match.MatchId));
        Assert.Contains(resumed.State.Changed, e => e.Id == drone.Id);
        Assert.Equal(match.State.OreCredits, resumed.State.OreCredits);
        var ok = (await player.PostAsync<List<OrderResultDto>>($"{Base}/matches/{match.MatchId}/orders",
            new OrderBatchRequest([Move(match, drone.Id, new HexCell(-3, 3))]))).Single();
        Assert.True(ok.Accepted, ok.Reason);
    }
}
