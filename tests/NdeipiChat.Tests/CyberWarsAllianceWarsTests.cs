using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.CyberWars;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// CyberWars Phase 02, Alliance Wars, against SRS v1.1 section 5.2 and the section 12 boxes:
/// home sectors and live owners on the world map (FR-2.1/2.1a), alliances and their war rooms
/// (FR-2.2), pings (FR-2.2a), war-room commands with /send-coop moving nothing (FR-2.2b), raids,
/// capture and the 24-hour protection (FR-2.3/2.3a), scrap crates that hold only play credits
/// (FR-2.3b), and the AI defending an absent player for up to 10 minutes (FR-2.4).
/// </summary>
public sealed class CyberWarsAllianceWarsTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = CyberWarsContract.BasePath;
    static readonly DateTimeOffset Now = new(2026, 10, 26, 9, 0, 0, TimeSpan.Zero);

    // ---- The engine ----

    static (MatchEngine Engine, Guid Attacker, Guid Defender) Raid(HexCell? sector = null)
    {
        var (attacker, defender) = (Guid.NewGuid(), Guid.NewGuid());
        var engine = MatchEngine.NewRaid(Guid.NewGuid(), ZambianProvince.Lusaka, sector ?? new HexCell(1, -1),
            new RaidSide(attacker, "Raider", PlayerRank.None), new RaidSide(defender, "Holder", PlayerRank.None), null, strikeApproved: false);
        return (engine, attacker, defender);
    }

    static OrderResultDto Send(MatchEngine engine, Guid userId, OrderKind kind, IReadOnlyList<Guid>? units = null, HexCell? target = null, Guid? entity = null) =>
        engine.Submit(userId, [new RtsOrder(Guid.NewGuid(), engine.State.Tick, kind, units ?? [], target, entity, null)], Now).Single();

    static List<MatchEntity> Owned(MatchEngine engine, Guid owner, string kind) => engine.State.Entities.Where(e => e.OwnerId == owner && e.Kind == kind).ToList();

    static void Clear(MatchEngine engine, Guid owner, params string[] kinds) =>
        engine.State.Entities.RemoveAll(e => e.OwnerId == owner && kinds.Contains(e.Kind));

    [Fact]
    public void A_raid_starts_with_the_defenders_base_and_a_landing_party()
    {
        var (engine, attacker, defender) = Raid();
        Assert.Equal(MatchModes.Raid, engine.State.Mode);
        Assert.Single(Owned(engine, defender, CyberWarsRules.CommandCenter));
        Assert.Single(Owned(engine, defender, CyberWarsRules.GuardTower));
        Assert.Single(Owned(engine, attacker, CyberWarsRules.Outpost));
        Assert.Equal(2, Owned(engine, attacker, CyberWarsRules.Engineer).Count);
        Assert.Equal(4, Owned(engine, attacker, CyberWarsRules.Squad).Count);

        // A saved base comes back as it was left, and the same sector is always the same ground.
        var saved = new List<BaseStructure> { new(CyberWarsRules.CommandCenter, 0, 0), new("war-factory", 2, 0), new(CyberWarsRules.PowerNode, 0, 2) };
        var rebuilt = MatchEngine.NewRaid(Guid.NewGuid(), ZambianProvince.Lusaka, new HexCell(1, -1),
            new RaidSide(attacker, "Raider", PlayerRank.None), new RaidSide(defender, "Holder", PlayerRank.None), saved, false);
        Assert.Contains(rebuilt.State.Entities, e => e.Kind == "war-factory" && e.At == new HexCell(MatchEngine.DefenderHome.Q + 2, MatchEngine.DefenderHome.R));
        Assert.Equal(engine.State.Rock, rebuilt.State.Rock);
    }

    [Fact]
    public void An_engineer_capturing_the_command_center_wins_the_raid()
    {
        var (engine, attacker, defender) = Raid();
        Clear(engine, defender, CyberWarsRules.Squad, CyberWarsRules.GuardTower);
        var engineer = Owned(engine, attacker, CyberWarsRules.Engineer)[0];
        var cc = Owned(engine, defender, CyberWarsRules.CommandCenter).Single();
        engineer.At = new HexCell(cc.At.Q + 2, cc.At.R - 1);
        engine.Step(); // Now the engineer sees it.

        var squad = Owned(engine, attacker, CyberWarsRules.Squad).Select(s => s.Id).ToList();
        Assert.Equal("Command Centers can't be destroyed: send an engineer to capture it.", Send(engine, attacker, OrderKind.Attack, squad, entity: cc.Id).Reason);
        Assert.Equal("Only saboteur engineers can capture.", Send(engine, attacker, OrderKind.Capture, squad, entity: cc.Id).Reason);
        Assert.True(Send(engine, attacker, OrderKind.Capture, [engineer.Id], entity: cc.Id).Accepted);

        for (var i = 0; i < 200 && !engine.IsOver; i++)
            engine.Step();
        Assert.True(engine.IsOver);
        Assert.Equal(attacker, engine.State.WinnerId);
        Assert.Equal(attacker, cc.OwnerId);
        Assert.Contains("captured", engine.State.EndReason);
        Assert.DoesNotContain(engine.State.Entities, e => e.Id == engineer.Id); // Spent on the capture.
    }

    [Fact]
    public void Destroyed_harvesters_and_rigs_drop_crates_of_ore_credits_or_gn_tokens_only()
    {
        // The type itself can't hold a real asset.
        Assert.Equal(["OreCredits", "GnTokens"], Enum.GetNames<CrateContents>());
        Assert.Equal(CrateContents.GnTokens, CyberWarsRules.ScrapFrom(CyberWarsRules.RigKey(RigRarity.Epic), 0)!.Contents);
        Assert.Null(CyberWarsRules.ScrapFrom(CyberWarsRules.Squad, 0));

        var (engine, attacker, defender) = Raid();
        var truck = Owned(engine, defender, CyberWarsRules.Harvester).Single();
        truck.Health = 1;
        truck.Path = [];
        truck.Task = UnitTasks.Idle;
        var squad = Owned(engine, attacker, CyberWarsRules.Squad)[0];
        squad.At = truck.At.Neighbours().First(c => !engine.State.Rock.Contains(c));
        var attackerPlayer = engine.Player(attacker)!;
        var before = attackerPlayer.Credits;

        for (var i = 0; i < 30 && engine.State.Entities.Contains(truck); i++)
            engine.Step();
        var crate = Assert.Single(engine.State.Entities, e => e.IsCrate);
        Assert.Equal(CrateContents.OreCredits, crate.Crate);
        Assert.Equal(truck.At, crate.At);

        // Walking over it collects it, as play credits.
        Assert.True(Send(engine, attacker, OrderKind.Move, [squad.Id], crate.At).Accepted);
        for (var i = 0; i < 40 && engine.State.Entities.Contains(crate); i++)
            engine.Step();
        Assert.DoesNotContain(crate, engine.State.Entities);
        Assert.True(attackerPlayer.Credits > before);
    }

    [Fact]
    public void A_thousand_raids_never_put_anything_but_play_credits_in_a_crate()
    {
        var seen = new HashSet<CrateContents?>();
        var crates = 0;
        var sectors = CyberWarsWorld.Sectors.ToList();
        for (var match = 0; match < 1000; match++)
        {
            var (engine, attacker, _) = Raid(sectors[match % sectors.Count]);
            // Everyone charges the enemy base; the defenders' AI meets them.
            var army = engine.State.Entities.Where(e => e.OwnerId == attacker && CyberWarsRules.Item(e.Kind) is { IsStructure: false }).Select(e => e.Id).ToList();
            Send(engine, attacker, OrderKind.AttackMove, army, MatchEngine.DefenderHome);
            foreach (var e in engine.State.Entities.Where(e => e.Kind.StartsWith("rig.") || e.Kind == CyberWarsRules.Harvester))
                e.Health = Math.Min(e.Health, 30);
            for (var tick = 0; tick < 120 && !engine.IsOver; tick++)
            {
                engine.Step();
                foreach (var c in engine.State.Entities.Where(e => e.IsCrate))
                {
                    seen.Add(c.Crate);
                    crates++;
                }
            }
            Assert.All(engine.State.Players, p => Assert.True(p.GnTokens >= 0));
        }
        Assert.True(crates > 0, "No crate dropped in the simulation.");
        Assert.Subset(new HashSet<CrateContents?> { CrateContents.OreCredits, CrateContents.GnTokens }, seen);
    }

    [Fact]
    public void An_absent_players_base_is_defended_by_the_ai_for_up_to_ten_minutes_and_they_can_take_it_back()
    {
        var (engine, attacker, defender) = Raid();
        var cc = Owned(engine, defender, CyberWarsRules.CommandCenter).Single();
        var intruder = Owned(engine, attacker, CyberWarsRules.Squad)[0];
        intruder.At = new HexCell(cc.At.Q + 3, cc.At.R - 1);
        for (var i = 0; i < 10; i++)
            engine.Step();

        // The defender never opened the game: the AI sends their squads at the intruder.
        Assert.Contains(defender, engine.DeltaFor(attacker).UnderAi!);
        Assert.Contains(Owned(engine, defender, CyberWarsRules.Squad), s => s.Task == UnitTasks.Attacking && s.TargetId == intruder.Id);

        // They come back: the AI hands over, and their orders are taken.
        engine.Touch(defender);
        engine.Step();
        Assert.DoesNotContain(defender, engine.DeltaFor(defender).UnderAi!);
        var squads = Owned(engine, defender, CyberWarsRules.Squad).Select(s => s.Id).ToList();
        Assert.True(Send(engine, defender, OrderKind.Stop, squads).Accepted);

        // Gone longer than 10 minutes: the AI stops, and their units stand down.
        var away = (long)(CyberWarsRules.AwayAfter + CyberWarsRules.AiDefendsFor).TotalSeconds * CyberWarsContract.TicksPerSecond;
        engine.Player(defender)!.LastSeenTick = engine.State.Tick - away - 10;
        engine.Step();
        Assert.DoesNotContain(defender, engine.DeltaFor(attacker).UnderAi!);
        intruder.At = cc.At.Neighbours().First(c => engine.State.Entities.All(e => e.At != c) && !engine.State.Rock.Contains(c));
        var health = intruder.Health;
        for (var i = 0; i < 30; i++)
            engine.Step();
        Assert.Equal(health, intruder.Health);
    }

    [Fact]
    public void When_time_runs_out_the_defender_holds()
    {
        var (engine, attacker, defender) = Raid();
        engine.Touch(attacker);
        engine.State.EndsAtTick = engine.State.Tick + 2;
        engine.Step();
        engine.Step();
        Assert.True(engine.IsOver);
        Assert.Equal(defender, engine.State.WinnerId);
        Assert.NotEqual(attacker, engine.State.WinnerId);
    }

    // ---- The API ----

    static string Unique(string name) => $"{name} {Guid.NewGuid():N}"[..(name.Length + 7)];

    static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    async Task<TestUser> PlayerAsync(string name, ZambianProvince province, HexCell sector)
    {
        var user = await app.CreateUserAsync(Unique(name));
        await user.PostAsync<SectorDto>($"{Base}/home", new ClaimHomeRequest(province, sector));
        return user;
    }

    async Task<MessageDto> SayAsync(TestUser user, Guid conversationId, string text) =>
        await user.PostAsync<MessageDto>($"api/conversations/{conversationId}/messages",
            new SendMessageRequest(conversationId, MessageKinds.Text, ContractJson.ToElement(new TextPayload(text)), Guid.NewGuid()));

    Task<List<CyberWarsCardPayload>> CardsAsync(Guid conversationId) => app.DbAsync(async db =>
        (await db.Messages.Where(m => m.ConversationId == conversationId && m.Kind == MessageKinds.CyberWarsCard).OrderBy(m => m.Seq).ToListAsync())
            .Select(m => ContractJson.Read<CyberWarsCardPayload>(m.PayloadJson)!).ToList());

    [Fact]
    public async Task A_sector_holds_one_base_and_its_owner_shows_live_on_the_province_map()
    {
        var first = await app.CreateUserAsync(Unique("Map Settler"));
        var second = await app.CreateUserAsync(Unique("Map Latecomer"));
        await using var hub = await app.ConnectAsync(second);
        await hub.InvokeAsync(ChatHubContract.Subscribe, CyberWarsContract.MapTopic(ZambianProvince.Western));
        var news = Wait.ForEventAsync<TopicMessageDto>(hub, nameof(IChatClient.TopicMessage),
            m => ContractJson.Read<MapEventDto>(m.Payload)?.Sector?.HolderId == first.Id);

        var claimed = await first.PostAsync<SectorDto>($"{Base}/home", new ClaimHomeRequest(ZambianProvince.Western, new HexCell(0, 1)));
        Assert.Equal((first.Id, first.Id), (claimed.HolderId, claimed.ControllerId));
        var update = ContractJson.Read<MapEventDto>((await news).Payload)!;
        Assert.Equal((MapEventKinds.Sector, new HexCell(0, 1)), (update.Kind, update.At));

        using var taken = await second.Http.PostAsJsonAsync($"{Base}/home", new ClaimHomeRequest(ZambianProvince.Western, new HexCell(0, 1)), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, taken.StatusCode);
        Assert.Contains("already", await ErrorOf(taken));

        var map = await second.GetAsync<WorldMapDto>($"{Base}/map");
        Assert.Equal(10, map.Provinces.Count);
        Assert.All(map.Provinces, p => Assert.Equal(19, p.Sectors.Count));
        Assert.Equal(first.Id, map.Provinces.Single(p => p.Province == ZambianProvince.Western).Sectors.Single(s => s.Sector == new HexCell(0, 1)).HolderId);
        var me = await first.GetAsync<CyberWarsMeDto>($"{Base}/me");
        Assert.Equal((ZambianProvince.Western, new HexCell(0, 1)), (me.HomeProvince!.Value, me.HomeSector!.Value));
    }

    [Fact]
    public async Task Joining_an_alliance_adds_the_war_room_to_chats_and_leaving_takes_it_away()
    {
        var founder = await PlayerAsync("Room Founder", ZambianProvince.Luapula, new HexCell(0, 0));
        var recruit = await PlayerAsync("Room Recruit", ZambianProvince.Luapula, new HexCell(1, 0));
        var alliance = await founder.PostAsync<AllianceDto>($"{Base}/alliances", new CreateAllianceRequest(Unique("Mansa Miners")));
        var room = await founder.GetAsync<ConversationDto>($"api/conversations/{alliance.ConversationId}");
        Assert.Equal(ConversationType.Group, room.Type);

        await using var hub = await app.ConnectAsync(recruit);
        var appears = Wait.ForEventAsync<ConversationDto>(hub, nameof(IChatClient.ConversationUpdated), c => c.Id == alliance.ConversationId, timeoutMs: 5000);
        var joined = await recruit.PostAsync<AllianceDto>($"{Base}/alliances/{alliance.Id}/join", new { });
        Assert.Equal(2, joined.Members.Count);
        await appears; // In Chats within 5 s (FR-2.2).
        Assert.Contains(await recruit.GetAsync<List<ConversationDto>>("api/conversations"), c => c.Id == alliance.ConversationId);
        await SayAsync(recruit, alliance.ConversationId, "Reporting for duty.");

        (await recruit.Http.PostAsync($"{Base}/alliances/{alliance.Id}/leave", null)).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await recruit.GetAsync<List<ConversationDto>>("api/conversations"), c => c.Id == alliance.ConversationId);
        using var after = await recruit.Http.PostAsJsonAsync($"api/conversations/{alliance.ConversationId}/messages",
            new SendMessageRequest(alliance.ConversationId, MessageKinds.Text, ContractJson.ToElement(new TextPayload("Still here?")), Guid.NewGuid()), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, after.StatusCode);
        Assert.Contains("member", await ErrorOf(after));
        Assert.Single((await founder.GetAsync<CyberWarsMeDto>($"{Base}/me")).Alliance!.Members);
    }

    [Fact]
    public async Task Send_coop_moves_nothing_and_only_opens_the_wallets_confirm_screen()
    {
        var sender = await PlayerAsync("Coop Sender", ZambianProvince.Muchinga, new HexCell(0, 0));
        var friend = await PlayerAsync("Coop Friend", ZambianProvince.Muchinga, new HexCell(1, 0));
        var alliance = await sender.PostAsync<AllianceDto>($"{Base}/alliances", new CreateAllianceRequest(Unique("Chinsali Gold")));
        await friend.PostAsync<AllianceDto>($"{Base}/alliances/{alliance.Id}/join", new { });
        var transfersBefore = await app.DbAsync(db => db.TokenTransfers.CountAsync());
        var bankBefore = await app.DbAsync(db => db.BankTransfers.CountAsync());
        var pointsBefore = await app.DbAsync(db => db.Points.CountAsync());

        await SayAsync(sender, alliance.ConversationId, $"/send-coop 50 @{friend.Name}");

        var card = Assert.Single(await CardsAsync(alliance.ConversationId));
        Assert.Equal($"chat/{alliance.ConversationId}/transfer?to={friend.Id}&amount=50&symbol=COOP", card.Route);
        Assert.Contains("50 Coop Coin", card.Title);
        Assert.Contains(friend.Name, card.Title);
        Assert.Contains("Nothing has moved", card.Text);
        Assert.Equal(transfersBefore, await app.DbAsync(db => db.TokenTransfers.CountAsync()));
        Assert.Equal(bankBefore, await app.DbAsync(db => db.BankTransfers.CountAsync()));
        Assert.Equal(pointsBefore, await app.DbAsync(db => db.Points.CountAsync()));
        Assert.DoesNotContain(app.Bridge.Requests, r => r.Method == HttpMethod.Post && r.Path.Contains("transfers"));

        // Nobody can post a card that looks like the game's.
        using var forged = await sender.Http.PostAsJsonAsync($"api/conversations/{alliance.ConversationId}/messages",
            new SendMessageRequest(alliance.ConversationId, MessageKinds.CyberWarsCard, ContractJson.ToElement(card), Guid.NewGuid()), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
    }

    [Fact]
    public async Task A_ping_posts_a_card_that_opens_the_game_at_that_sector()
    {
        var scout = await PlayerAsync("Ping Scout", ZambianProvince.Northern, new HexCell(0, 0));
        var alliance = await scout.PostAsync<AllianceDto>($"{Base}/alliances", new CreateAllianceRequest(Unique("Kasama Scouts")));
        (await scout.Http.PostAsJsonAsync($"{Base}/pings", new AlliancePingRequest(new HexCell(2, -1), "Rich gold here", ZambianProvince.Northern), ContractJson.Options)).EnsureSuccessStatusCode();
        var card = Assert.Single(await CardsAsync(alliance.ConversationId));
        Assert.Equal("apps/cyberwars?sector=Northern:2:-1", card.Route);
        Assert.Equal("Rich gold here", card.Text);
        Assert.True(CyberWarsWorld.TryParseSector("Northern:2:-1", out var province, out var sector) && province == ZambianProvince.Northern && sector == new HexCell(2, -1));
    }

    [Fact]
    public async Task A_raid_captures_the_sector_protects_the_loser_for_24_hours_and_refuses_a_second_raid_with_the_end_time()
    {
        var raider = await PlayerAsync("Raid Leader", ZambianProvince.Southern, new HexCell(-1, 0));
        var ally = await PlayerAsync("Raid Ally", ZambianProvince.Southern, new HexCell(-2, 1));
        var holder = await PlayerAsync("Raid Holder", ZambianProvince.Southern, new HexCell(1, 0));
        var stranger = await PlayerAsync("Raid Elsewhere", ZambianProvince.Central, new HexCell(0, 0));
        var alliance = await raider.PostAsync<AllianceDto>($"{Base}/alliances", new CreateAllianceRequest(Unique("Choma Raiders")));
        await ally.PostAsync<AllianceDto>($"{Base}/alliances/{alliance.Id}/join", new { });

        // Only contested sectors: a rival in the raider's own province, not an ally.
        using (var far = await raider.Http.PostAsJsonAsync($"{Base}/raids", new StartRaidRequest(ZambianProvince.Central, new HexCell(0, 0)), ContractJson.Options))
            Assert.Contains("contested", await ErrorOf(far));
        using (var friendly = await raider.Http.PostAsJsonAsync($"{Base}/raids", new StartRaidRequest(ZambianProvince.Southern, new HexCell(-2, 1)), ContractJson.Options))
            Assert.Contains("your own alliance", await ErrorOf(friendly));

        // A strike vote with both members behind it lands extra squads.
        await SayAsync(raider, alliance.ConversationId, "/vote-strike Southern 1,0");
        await SayAsync(ally, alliance.ConversationId, "/vote-strike");
        Assert.NotNull((await raider.GetAsync<CyberWarsMeDto>($"{Base}/me")).Alliance!.Strike!.ApprovedUntil);

        var raid = await raider.PostAsync<MatchStateDto>($"{Base}/raids", new StartRaidRequest(ZambianProvince.Southern, new HexCell(1, 0)));
        Assert.Equal(MatchModes.Raid, raid.Mode);
        Assert.Equal([MatchSides.Defender, MatchSides.Attacker], raid.Players!.Select(p => p.Side));
        Assert.Equal(6, raid.State.Changed.Count(e => e.Kind == CyberWarsRules.Squad && e.OwnerId == raider.Id));
        var defending = (await holder.GetAsync<CyberWarsMeDto>($"{Base}/me")).Matches!.Single();
        Assert.Equal((raid.MatchId, MatchSides.Defender), (defending.MatchId, defending.Side));

        // /reinforce lands two more squads beside the raider.
        await SayAsync(ally, alliance.ConversationId, $"/reinforce @{raider.Name}");
        Assert.Contains("Reinforcements", (await CardsAsync(alliance.ConversationId)).Last().Title);

        // The raider takes the Command Center.
        var matches = app.Services.GetRequiredService<CyberWarsMatches>();
        await app.DbAsync(async db =>
        {
            var live = (await matches.GetAsync(raid.MatchId, db, default))!;
            lock (live.Gate)
            {
                Assert.Equal(8, live.Engine.State.Entities.Count(e => e.OwnerId == raider.Id && e.Kind == CyberWarsRules.Squad));
                live.Engine.State.Entities.First(e => e.OwnerId == holder.Id && e.Kind == CyberWarsRules.CommandCenter).OwnerId = raider.Id;
            }
        });
        await Wait.UntilAsync(async () => await app.DbAsync(db => db.CyberWarsMatches.AnyAsync(m => m.Id == raid.MatchId && m.EndedAt != null)), "the raid is settled");

        var match = await app.DbAsync(db => db.CyberWarsMatches.SingleAsync(m => m.Id == raid.MatchId));
        Assert.Equal(raider.Id, match.WinnerUserId);
        var sector = (await raider.GetAsync<WorldMapDto>($"{Base}/map")).Provinces.Single(p => p.Province == ZambianProvince.Southern).Sectors.Single(s => s.Sector == new HexCell(1, 0));
        Assert.Equal((holder.Id, raider.Id, alliance.Id), (sector.HolderId!.Value, sector.ControllerId!.Value, sector.AllianceId!.Value));
        var until = (await holder.GetAsync<CyberWarsMeDto>($"{Base}/me")).ProtectedUntil!.Value;
        Assert.InRange(until - DateTimeOffset.UtcNow, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24));

        // Raiding the protected base again is refused, with the time the protection ends.
        using var again = await stranger.Http.PostAsJsonAsync($"{Base}/home", new ClaimHomeRequest(ZambianProvince.Southern, new HexCell(0, 2)), ContractJson.Options);
        again.EnsureSuccessStatusCode();
        using var refused = await stranger.Http.PostAsJsonAsync($"{Base}/raids", new StartRaidRequest(ZambianProvince.Southern, new HexCell(1, 0)), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains($"protected until {until.UtcDateTime:yyyy-MM-dd HH:mm} UTC", await ErrorOf(refused));
    }
}
