using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>
/// Phase 02, Alliance Wars: the 10-province world map and home sectors (FR-2.1), alliances whose
/// war room is a group chat (FR-2.2), map pings (FR-2.2a) and raids on rival bases (FR-2.3).
/// Nothing here touches NDX, Coop Coin, Believe Points or the wallet (section 8).
/// </summary>
public sealed class CyberWarsWorldService(
    ChatDbContext db, CyberWarsMatches matches, ChatNotifier notifier, ConversationService conversations,
    CyberWarsCards cards, TimeProvider clock)
{
    // ---- The map (FR-2.1, FR-2.1a) ----

    public async Task<WorldMapDto> MapAsync(CancellationToken ct)
    {
        var sectors = await SectorsAsync(null, ct);
        return new WorldMapDto(Enum.GetValues<ZambianProvince>()
            .Select(p => new ProvinceMapDto(p, sectors.Where(s => s.Province == p).ToList())).ToList());
    }

    /// <summary>Every sector of a province (or all of them), with its base, controller, alliance and any raid on it.</summary>
    public async Task<List<SectorDto>> SectorsAsync(ZambianProvince? province, CancellationToken ct)
    {
        var bases = await db.CyberWarsBases.Where(b => province == null || b.Province == province)
            .Join(db.Users, b => b.UserId, u => u.Id, (b, u) => new { b.Province, b.SectorQ, b.SectorR, b.UserId, u.DisplayName })
            .ToListAsync(ct);
        var controls = await db.CyberWarsSectorControls.Where(c => province == null || c.Province == province)
            .Join(db.Users, c => c.ControllerUserId, u => u.Id, (c, u) => new { c.Province, c.SectorQ, c.SectorR, c.ControllerUserId, u.DisplayName })
            .ToListAsync(ct);
        var people = bases.Select(b => b.UserId).Concat(controls.Select(c => c.ControllerUserId)).Distinct().ToList();
        var alliances = await db.CyberWarsAllianceMembers.Where(m => people.Contains(m.UserId))
            .Join(db.CyberWarsAlliances, m => m.AllianceId, a => a.Id, (m, a) => new { m.UserId, a.Id, a.Name })
            .ToDictionaryAsync(x => x.UserId, ct);
        var protection = await db.CyberWarsPlayers.Where(p => people.Contains(p.UserId) && p.ProtectedUntil != null)
            .ToDictionaryAsync(p => p.UserId, p => p.ProtectedUntil, ct);
        var raids = await db.CyberWarsMatches
            .Where(m => m.Mode == MatchModes.Raid && m.Status == MatchStatuses.Running && (province == null || m.Province == province))
            .Select(m => new { m.Id, m.Province, m.SectorQ, m.SectorR }).ToListAsync(ct);

        var now = clock.GetUtcNow();
        var result = new List<SectorDto>();
        foreach (var p in province is { } one ? [one] : Enum.GetValues<ZambianProvince>())
            foreach (var cell in CyberWarsWorld.Sectors)
            {
                var holder = bases.FirstOrDefault(b => b.Province == p && b.SectorQ == cell.Q && b.SectorR == cell.R);
                var control = controls.FirstOrDefault(c => c.Province == p && c.SectorQ == cell.Q && c.SectorR == cell.R);
                if (holder is null && control is null)
                {
                    result.Add(new SectorDto(p, cell, null, null, null, null, null, null, null, null));
                    continue;
                }
                var (controllerId, controllerName) = control is not null ? (control.ControllerUserId, control.DisplayName) : (holder!.UserId, holder.DisplayName);
                var alliance = alliances.GetValueOrDefault(controllerId);
                var until = holder is not null ? protection.GetValueOrDefault(holder.UserId) : null;
                var raid = raids.FirstOrDefault(r => r.Province == p && r.SectorQ == cell.Q && r.SectorR == cell.R)?.Id;
                result.Add(new SectorDto(p, cell, holder?.UserId, holder?.DisplayName, controllerId, controllerName,
                    alliance?.Id, alliance?.Name, until > now ? until : null, raid));
            }
        return result;
    }

    async Task<SectorDto> SectorAsync(ZambianProvince province, HexCell sector, CancellationToken ct) =>
        (await SectorsAsync(province, ct)).Single(s => s.Sector == sector);

    Task PublishAsync(MapEventDto e) => notifier.PublishAsync(CyberWarsContract.MapTopic(e.Province), e);

    /// <summary>Picks the player's home sector (FR-2.1). A sector holds at most one base.</summary>
    public async Task<SectorDto> ClaimHomeAsync(User user, ClaimHomeRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Province))
            throw new ChatRejectedException("Pick one of Zambia's ten provinces.");
        if (!CyberWarsWorld.IsSector(request.Sector))
            throw new ChatRejectedException("That sector isn't on the map.");
        var player = await PlayerAsync(user, ct);
        if (await db.CyberWarsMatchPlayers.AnyAsync(p => p.UserId == user.Id && p.Side != MatchSides.Solo
                && db.CyberWarsMatches.Any(m => m.Id == p.MatchId && m.Status == MatchStatuses.Running), ct))
            throw new ChatRejectedException("Finish your raid before you move your base.");
        if (await db.CyberWarsBases.AnyAsync(b => b.Province == request.Province && b.SectorQ == request.Sector.Q && b.SectorR == request.Sector.R && b.UserId != user.Id, ct))
            throw new ChatRejectedException("Someone's base is already in that sector.");

        var now = clock.GetUtcNow();
        var home = await db.CyberWarsBases.FirstOrDefaultAsync(b => b.UserId == user.Id, ct);
        var old = home is null ? ((ZambianProvince, HexCell)?)null : (home.Province, new HexCell(home.SectorQ, home.SectorR));
        if (home is null)
            db.CyberWarsBases.Add(home = new CyberWarsBase { UserId = user.Id });
        (home.Province, home.SectorQ, home.SectorR, home.UpdatedAt) = (request.Province, request.Sector.Q, request.Sector.R, now);
        player.HomeProvince = request.Province;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            throw new ChatRejectedException("Someone's base is already in that sector.");
        }

        if (old is { } was && was != (request.Province, request.Sector))
            await PublishAsync(new MapEventDto(MapEventKinds.Sector, was.Item1, was.Item2, await SectorAsync(was.Item1, was.Item2, ct), null, null));
        var claimed = await SectorAsync(request.Province, request.Sector, ct);
        await PublishAsync(new MapEventDto(MapEventKinds.Sector, request.Province, request.Sector, claimed, null, null));
        return claimed;
    }

    // ---- Alliances and war rooms (FR-2.2) ----

    public async Task<AllianceDto?> AllianceOfAsync(Guid userId, CancellationToken ct)
    {
        var allianceId = await db.CyberWarsAllianceMembers.Where(m => m.UserId == userId).Select(m => (Guid?)m.AllianceId).FirstOrDefaultAsync(ct);
        return allianceId is { } id ? await AllianceAsync(id, ct) : null;
    }

    public async Task<AllianceDto?> AllianceAsync(Guid allianceId, CancellationToken ct)
    {
        var alliance = await db.CyberWarsAlliances.FirstOrDefaultAsync(a => a.Id == allianceId, ct);
        if (alliance is null)
            return null;
        var members = await db.CyberWarsAllianceMembers.Where(m => m.AllianceId == allianceId)
            .Join(db.Users, m => m.UserId, u => u.Id, (m, u) => new { m.UserId, u.DisplayName, m.JoinedAt })
            .OrderBy(m => m.JoinedAt).ToListAsync(ct);
        var strike = await db.CyberWarsStrikes.FirstOrDefaultAsync(s => s.AllianceId == allianceId, ct);
        StrikeDto? strikeDto = null;
        if (strike is not null)
        {
            var votes = await db.CyberWarsStrikeBallots.CountAsync(b => b.AllianceId == allianceId, ct);
            var approved = strike.ApprovedUntil > clock.GetUtcNow() ? strike.ApprovedUntil : null;
            strikeDto = new StrikeDto(strike.Province, new HexCell(strike.SectorQ, strike.SectorR), votes, members.Count / 2 + 1, approved);
        }
        return new AllianceDto(alliance.Id, alliance.Name, alliance.Province, alliance.ConversationId,
            members.Select(m => new AllianceMemberDto(m.UserId, m.DisplayName, m.UserId == alliance.LeaderId)).ToList(), strikeDto);
    }

    public async Task<IReadOnlyList<AllianceDto>> AlliancesAsync(ZambianProvince? province, CancellationToken ct)
    {
        var ids = await db.CyberWarsAlliances.Where(a => province == null || a.Province == province).OrderBy(a => a.Name).Select(a => a.Id).ToListAsync(ct);
        var list = new List<AllianceDto>();
        foreach (var id in ids)
            list.Add((await AllianceAsync(id, ct))!);
        return list;
    }

    /// <summary>Founds an alliance in the player's home province, with its war room in Ndeipi Chat.</summary>
    public async Task<AllianceDto> CreateAllianceAsync(User user, CreateAllianceRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 3 or > 40)
            throw new ChatRejectedException("Give the alliance a name of 3 to 40 characters.");
        var player = await PlayerAsync(user, ct);
        if (player.HomeProvince is not { } province)
            throw new ChatRejectedException("Pick your home sector on the map first.");
        if (await db.CyberWarsAllianceMembers.AnyAsync(m => m.UserId == user.Id, ct))
            throw new ChatRejectedException("Leave your alliance before you found another.");
        if (await db.CyberWarsAlliances.AnyAsync(a => a.Name == name, ct))
            throw new ChatRejectedException("An alliance already has that name.");

        var room = await conversations.CreateGroupForAsync(user, $"⚔️ {name} war room", ct);
        var alliance = new CyberWarsAlliance { Id = Guid.NewGuid(), Name = name, Province = province, ConversationId = room, LeaderId = user.Id, CreatedAt = clock.GetUtcNow() };
        db.CyberWarsAlliances.Add(alliance);
        db.CyberWarsAllianceMembers.Add(new CyberWarsAllianceMember { UserId = user.Id, AllianceId = alliance.Id, JoinedAt = alliance.CreatedAt });
        await db.SaveChangesAsync(ct);
        await PublishHomeAsync(user.Id, ct);
        return (await AllianceAsync(alliance.Id, ct))!;
    }

    /// <summary>Joins an alliance and its war room, which then shows in the player's Chats (FR-2.2).</summary>
    public async Task<AllianceDto?> JoinAsync(User user, Guid allianceId, CancellationToken ct)
    {
        var alliance = await db.CyberWarsAlliances.FirstOrDefaultAsync(a => a.Id == allianceId, ct);
        if (alliance is null)
            return null;
        if (await db.CyberWarsAllianceMembers.FirstOrDefaultAsync(m => m.UserId == user.Id, ct) is { } current)
        {
            if (current.AllianceId == allianceId)
                return await AllianceAsync(allianceId, ct);
            throw new ChatRejectedException("Leave your alliance before you join another.");
        }
        db.CyberWarsAllianceMembers.Add(new CyberWarsAllianceMember { UserId = user.Id, AllianceId = allianceId, JoinedAt = clock.GetUtcNow() });
        await db.SaveChangesAsync(ct);
        await conversations.AddMemberAsync(alliance.ConversationId, user, ct);
        await PublishHomeAsync(user.Id, ct);
        return await AllianceAsync(allianceId, ct);
    }

    /// <summary>Leaves the alliance and loses the war room. The last one out closes the alliance.</summary>
    public async Task<bool> LeaveAsync(User user, Guid allianceId, CancellationToken ct)
    {
        var membership = await db.CyberWarsAllianceMembers.FirstOrDefaultAsync(m => m.UserId == user.Id && m.AllianceId == allianceId, ct);
        if (membership is null)
            return false;
        var alliance = await db.CyberWarsAlliances.FirstAsync(a => a.Id == allianceId, ct);
        db.CyberWarsAllianceMembers.Remove(membership);
        await db.CyberWarsStrikeBallots.Where(b => b.AllianceId == allianceId && b.UserId == user.Id).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);
        var next = await db.CyberWarsAllianceMembers.Where(m => m.AllianceId == allianceId).OrderBy(m => m.JoinedAt).FirstOrDefaultAsync(ct);
        if (next is null)
        {
            db.CyberWarsAlliances.Remove(alliance);
            await db.SaveChangesAsync(ct);
        }
        else if (alliance.LeaderId == user.Id)
        {
            alliance.LeaderId = next.UserId;
            await db.SaveChangesAsync(ct);
        }
        await conversations.RemoveMemberAsync(alliance.ConversationId, user, ct);
        await PublishHomeAsync(user.Id, ct);
        return true;
    }

    /// <summary>The player's home sector shows their alliance, so a change is news on the map.</summary>
    async Task PublishHomeAsync(Guid userId, CancellationToken ct)
    {
        if (await db.CyberWarsBases.FirstOrDefaultAsync(b => b.UserId == userId, ct) is { } home)
        {
            var cell = new HexCell(home.SectorQ, home.SectorR);
            await PublishAsync(new MapEventDto(MapEventKinds.Sector, home.Province, cell, await SectorAsync(home.Province, cell, ct), null, null));
        }
    }

    // ---- Pings (FR-2.2a) ----

    /// <summary>Posts a map point to the war room as a card that opens the game there, and to the province's map topic.</summary>
    public async Task<bool> PingAsync(User user, AlliancePingRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Province) || !CyberWarsWorld.IsSector(request.At))
            throw new ChatRejectedException("That point isn't on the map.");
        var text = request.Message?.Trim() ?? "";
        if (text.Length > 140)
            throw new ChatRejectedException("Keep the ping under 140 characters.");
        var alliance = await AllianceOfAsync(user.Id, ct) ?? throw new ChatRejectedException("Join an alliance to ping its war room.");
        var place = CyberWarsWorld.SectorName(request.Province, request.At);
        await cards.PostAsync(user, alliance.ConversationId, new CyberWarsCardPayload("📍", $"{user.DisplayName} pinged {place}",
            text.Length > 0 ? text : "Look here.", CyberWarsWorld.SectorRoute(request.Province, request.At), "Open the map"), ct);
        await PublishAsync(new MapEventDto(MapEventKinds.Ping, request.Province, request.At, null, $"{user.DisplayName}: {text}", null));
        return true;
    }

    // ---- Raids (FR-2.3, FR-2.3a) ----

    /// <summary>
    /// Starts a raid on a rival's base in a contested sector: one in the raider's own home province,
    /// held by someone outside their alliance, whose 24-hour protection after a loss has run out.
    /// </summary>
    public async Task<(Guid MatchId, MatchEngine Engine, DateTimeOffset StartedAt)> StartRaidAsync(User user, StartRaidRequest request, CancellationToken ct)
    {
        if (!Enum.IsDefined(request.Province) || !CyberWarsWorld.IsSector(request.Sector))
            throw new ChatRejectedException("That sector isn't on the map.");
        var me = await PlayerAsync(user, ct);
        var myHome = await db.CyberWarsBases.FirstOrDefaultAsync(b => b.UserId == user.Id, ct)
            ?? throw new ChatRejectedException("Pick your home sector on the map before you raid.");
        var target = await db.CyberWarsBases.FirstOrDefaultAsync(b => b.Province == request.Province && b.SectorQ == request.Sector.Q && b.SectorR == request.Sector.R, ct)
            ?? throw new ChatRejectedException("Nobody's base is in that sector.");
        if (target.UserId == user.Id)
            throw new ChatRejectedException("That's your own base.");
        if (target.Province != myHome.Province)
            throw new ChatRejectedException($"Raids are only on contested sectors: rivals in your home province, {CyberWarsWorld.ProvinceName(myHome.Province)}.");
        var mine = await db.CyberWarsAllianceMembers.Where(m => m.UserId == user.Id).Select(m => (Guid?)m.AllianceId).FirstOrDefaultAsync(ct);
        if (mine is { } ally && await db.CyberWarsAllianceMembers.AnyAsync(m => m.UserId == target.UserId && m.AllianceId == ally, ct))
            throw new ChatRejectedException("That base belongs to your own alliance.");
        var defender = await PlayerAsync(target.UserId, ct);
        var now = clock.GetUtcNow();
        if (defender.ProtectedUntil is { } until && until > now)
            throw new ChatRejectedException($"That base is protected until {until.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC, 24 hours after its last loss.");
        if (await db.CyberWarsMatches.AnyAsync(m => m.Mode == MatchModes.Raid && m.Status == MatchStatuses.Running
                && m.Province == request.Province && m.SectorQ == request.Sector.Q && m.SectorR == request.Sector.R, ct))
            throw new ChatRejectedException("Someone is already raiding that base.");
        if (await db.CyberWarsMatchPlayers.AnyAsync(p => p.UserId == user.Id && p.Side == MatchSides.Attacker
                && db.CyberWarsMatches.Any(m => m.Id == p.MatchId && m.Status == MatchStatuses.Running), ct))
            throw new ChatRejectedException("Finish your raid before you start another.");

        var defenderUser = await db.Users.FirstAsync(u => u.Id == target.UserId, ct);
        var layout = ContractJson.Read<List<BaseStructure>>(target.LayoutJson);
        var strike = mine is { } a && await db.CyberWarsStrikes.AnyAsync(s => s.AllianceId == a && s.ApprovedUntil > now
            && s.Province == request.Province && s.SectorQ == request.Sector.Q && s.SectorR == request.Sector.R, ct);

        var match = new CyberWarsMatch
        {
            Id = Guid.NewGuid(), Province = request.Province, Day = DateOnly.FromDateTime(now.UtcDateTime), Mode = MatchModes.Raid,
            SectorQ = request.Sector.Q, SectorR = request.Sector.R, StartedAt = now
        };
        var engine = MatchEngine.NewRaid(match.Id, request.Province, request.Sector,
            new RaidSide(user.Id, user.DisplayName, me.Rank), new RaidSide(defenderUser.Id, defenderUser.DisplayName, defender.Rank), layout, strike);
        engine.Touch(user.Id);
        db.CyberWarsMatches.Add(match);
        db.CyberWarsMatchPlayers.Add(new CyberWarsMatchPlayer { MatchId = match.Id, UserId = user.Id, Side = MatchSides.Attacker });
        db.CyberWarsMatchPlayers.Add(new CyberWarsMatchPlayer { MatchId = match.Id, UserId = defenderUser.Id, Side = MatchSides.Defender });
        db.CyberWarsSnapshots.Add(new CyberWarsSnapshot { MatchId = match.Id, Tick = 0, StateBlob = ContractJson.Write(engine.State), At = now });
        await db.SaveChangesAsync(ct);
        matches.Add(engine);

        var place = CyberWarsWorld.SectorName(request.Province, request.Sector);
        await PublishAsync(new MapEventDto(MapEventKinds.RaidStarted, request.Province, request.Sector, await SectorAsync(request.Province, request.Sector, ct),
            $"{user.DisplayName} is raiding {defenderUser.DisplayName}", match.Id));
        var route = $"apps/{CyberWarsContract.AppId}?match={match.Id:N}";
        if (await AllianceOfAsync(defenderUser.Id, ct) is { } theirs)
            await cards.PostAsync(defenderUser, theirs.ConversationId, new CyberWarsCardPayload("🚨", $"{defenderUser.DisplayName}'s base is under attack",
                $"{user.DisplayName} is raiding {place}. The base defends itself for 10 minutes; open the game to take command.", route, "Defend the base"), ct);
        if (await AllianceOfAsync(user.Id, ct) is { } ours)
            await cards.PostAsync(user, ours.ConversationId, new CyberWarsCardPayload("⚔️", $"{user.DisplayName} launched a raid",
                $"Target: {defenderUser.DisplayName}'s base at {place}.{(strike ? " The alliance's strike vote sends extra squads." : "")}",
                CyberWarsWorld.SectorRoute(request.Province, request.Sector), "Watch the map"), ct);
        return (match.Id, engine, now);
    }

    /// <summary>
    /// Settles a finished match once: GN Tokens from scrap crates go to the accounts, and a raid moves
    /// the sector and starts the loser's 24-hour protection (FR-2.1a, FR-2.3a).
    /// </summary>
    public async Task SettleAsync(MatchState state, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var claimed = await db.CyberWarsMatches.Where(m => m.Id == state.MatchId && m.EndedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, state.Status).SetProperty(m => m.EndedAt, now).SetProperty(m => m.WinnerUserId, state.WinnerId), ct);
        if (claimed == 0)
            return;

        foreach (var p in state.Players.Where(p => p.GnTokens > 0))
        {
            var account = await PlayerAsync(p.UserId, ct);
            account.GnTokens += p.GnTokens;
        }
        await db.SaveChangesAsync(ct);

        if (state.Mode != MatchModes.Raid || state.Sector is not { } sector)
            return;
        var attacker = state.Players.Single(p => p.Side == MatchSides.Attacker);
        var defender = state.Players.Single(p => p.Side == MatchSides.Defender);
        var control = await db.CyberWarsSectorControls.FirstOrDefaultAsync(c => c.Province == state.Province && c.SectorQ == sector.Q && c.SectorR == sector.R, ct);
        if (state.WinnerId == attacker.UserId)
        {
            if (control is null)
                db.CyberWarsSectorControls.Add(control = new CyberWarsSectorControl { Province = state.Province, SectorQ = sector.Q, SectorR = sector.R });
            (control.ControllerUserId, control.MatchId, control.CapturedAt) = (attacker.UserId, state.MatchId, now);
            (await PlayerAsync(defender.UserId, ct)).ProtectedUntil = now + CyberWarsRules.HomeProtection;
        }
        else if (control is not null)
            db.CyberWarsSectorControls.Remove(control); // Defended: the holder has its Command Center back.
        await db.SaveChangesAsync(ct);

        var dto = await SectorAsync(state.Province, sector, ct);
        await PublishAsync(new MapEventDto(MapEventKinds.Sector, state.Province, sector, dto, null, null));
        await PublishAsync(new MapEventDto(MapEventKinds.RaidEnded, state.Province, sector, dto, state.EndReason, state.MatchId));
        var place = CyberWarsWorld.SectorName(state.Province, sector);
        var won = state.WinnerId == attacker.UserId;
        foreach (var side in new[] { attacker, defender })
            if (await AllianceOfAsync(side.UserId, ct) is { } alliance && await db.Users.FirstOrDefaultAsync(u => u.Id == side.UserId, ct) is { } member)
                await cards.PostAsync(member, alliance.ConversationId, new CyberWarsCardPayload(won == (side == attacker) ? "🏆" : "🛡️",
                    won ? $"{attacker.Name} took {place}" : $"{defender.Name} held {place}", state.EndReason ?? "The raid is over.",
                    CyberWarsWorld.SectorRoute(state.Province, sector), "Open the map"), ct);
    }

    /// <summary>The structures of the player's skirmish base, kept as their home base for raids.</summary>
    public Task SaveBaseAsync(Guid userId, IReadOnlyList<BaseStructure> layout, CancellationToken ct) =>
        db.CyberWarsBases.Where(b => b.UserId == userId).ExecuteUpdateAsync(s => s
            .SetProperty(b => b.LayoutJson, ContractJson.Write(layout))
            .SetProperty(b => b.Version, b => b.Version + 1)
            .SetProperty(b => b.UpdatedAt, clock.GetUtcNow()), ct);

    public async Task<CyberWarsPlayer> PlayerAsync(User user, CancellationToken ct) => await PlayerAsync(user.Id, ct);

    public async Task<CyberWarsPlayer> PlayerAsync(Guid userId, CancellationToken ct)
    {
        if (await db.CyberWarsPlayers.FirstOrDefaultAsync(p => p.UserId == userId, ct) is { } player)
            return player;
        player = new CyberWarsPlayer { UserId = userId, CreatedAt = clock.GetUtcNow() };
        db.CyberWarsPlayers.Add(player);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.Entry(player).State = EntityState.Detached;
            player = await db.CyberWarsPlayers.FirstAsync(p => p.UserId == userId, ct);
        }
        return player;
    }
}

/// <summary>
/// Posts CyberWars cards into chats. Only the server sends them: a card a client sends is refused,
/// so a card in a war room can always be trusted to come from the game.
/// </summary>
public sealed class CyberWarsCards(IServiceProvider services)
{
    static readonly AsyncLocal<bool> Posting = new();

    public static bool IsPosting => Posting.Value;

    public async Task PostAsync(User sender, Guid conversationId, CyberWarsCardPayload card, CancellationToken ct)
    {
        Posting.Value = true;
        try
        {
            // Resolved here, not injected: the message pipeline reaches back to the war room's commands.
            await services.GetRequiredService<MessageService>().SendAsync(sender,
                new SendMessageRequest(conversationId, MessageKinds.CyberWarsCard, ContractJson.ToElement(card), Guid.NewGuid()), ct);
        }
        finally
        {
            Posting.Value = false;
        }
    }
}

public sealed class CyberWarsCardHandler : IMessageKindHandler
{
    public string Kind => MessageKinds.CyberWarsCard;

    public Task<PreparedMessage> PrepareAsync(MessageContext context, JsonElement payload, CancellationToken ct)
    {
        if (!CyberWarsCards.IsPosting)
            throw new ChatRejectedException("CyberWars cards come from the game.");
        var card = MessagePayload.Read<CyberWarsCardPayload>(payload);
        var game = card.Route.StartsWith($"apps/{CyberWarsContract.AppId}", StringComparison.Ordinal);
        var transfer = card.Route.StartsWith($"chat/{context.Conversation.Id}/transfer?", StringComparison.Ordinal);
        if (!game && !transfer)
            throw new ChatRejectedException("A CyberWars card opens the game or this chat's transfer screen.");
        return Task.FromResult(new PreparedMessage(payload));
    }
}
