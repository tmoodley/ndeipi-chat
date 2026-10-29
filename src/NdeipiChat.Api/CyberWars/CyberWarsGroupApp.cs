using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>
/// The war room as a community: the alliance's province, home and sectors in its header, members
/// listed by rank with what they're doing in the game, and a War room tab. The alliance decides
/// who's in the room, so leaving the room or being removed from it is leaving the alliance.
/// </summary>
public sealed class CyberWarsGroupApp(ChatDbContext db, CyberWarsWorldService world, ConversationService conversations) : IGroupApp
{
    public string AppId => CyberWarsContract.AppId;

    public async Task<GroupAppView?> DescribeAsync(ChatGroup group, IReadOnlyList<Guid> memberIds, CancellationToken ct)
    {
        var alliance = await db.CyberWarsAlliances.AsNoTracking().FirstOrDefaultAsync(a => a.Id == group.AppRef, ct);
        if (alliance is null)
            return null;

        var ranks = await db.CyberWarsPlayers.AsNoTracking().Where(p => memberIds.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.Rank, ct);
        var bases = await db.CyberWarsBases.AsNoTracking().Where(b => memberIds.Contains(b.UserId)).ToDictionaryAsync(b => b.UserId, ct);
        var raids = await db.CyberWarsMatchPlayers.AsNoTracking()
            .Where(p => memberIds.Contains(p.UserId) && p.Side != MatchSides.Solo)
            .Join(db.CyberWarsMatches.Where(m => m.Status == MatchStatuses.Running && m.Mode == MatchModes.Raid),
                p => p.MatchId, m => m.Id, (p, m) => new { p.UserId, p.Side, m.Province, m.SectorQ, m.SectorR })
            .ToListAsync(ct);

        var members = new Dictionary<Guid, GroupAppMember>();
        foreach (var id in memberIds)
        {
            var (section, order, rank) = id == alliance.LeaderId
                ? ("Leader", 0, "Leader")
                : ranks.GetValueOrDefault(id) switch
                {
                    PlayerRank.Sovereign => ("Sovereigns", 1, "Sovereign"),
                    PlayerRank.Commander => ("Commanders", 2, "Commander"),
                    PlayerRank.Explorer => ("Explorers", 3, "Explorer"),
                    _ => ("Recruits", 4, "Recruit")
                };
            var raid = raids.FirstOrDefault(r => r.UserId == id);
            var status = raid is { SectorQ: { } q, SectorR: { } r }
                ? $"{(raid.Side == MatchSides.Attacker ? "Raiding" : "Defending")} {CyberWarsWorld.SectorName(raid.Province, new HexCell(q, r))}"
                : null;
            var home = bases.GetValueOrDefault(id);
            members[id] = new GroupAppMember(section, order, rank, status,
                home is null ? null : CyberWarsWorld.SectorRoute(home.Province, new HexCell(home.SectorQ, home.SectorR)));
        }

        var leaderHome = bases.GetValueOrDefault(alliance.LeaderId);
        var facts = new List<GroupFactDto> { new("Province", CyberWarsWorld.ProvinceName(alliance.Province)) };
        if (leaderHome is not null)
            facts.Add(new("Home sector", CyberWarsWorld.SectorName(leaderHome.Province, new HexCell(leaderHome.SectorQ, leaderHome.SectorR))));
        facts.Add(new("Sectors held", (await world.SectorsHeldAsync(alliance.Id, ct)).ToString()));

        var map = leaderHome is null
            ? $"apps/{AppId}"
            : CyberWarsWorld.SectorRoute(leaderHome.Province, new HexCell(leaderHome.SectorQ, leaderHome.SectorR));
        return new GroupAppView(facts, new GroupLinkDto("Open map", map), new GroupAppTabDto(AppId, "War room", "⚔️"),
            "Players join from the alliance screen in CyberWars.", members);
    }

    public async Task RemoveMemberAsync(ChatGroup group, User member, CancellationToken ct)
    {
        if (group.AppRef is not { } allianceId || !await world.LeaveAsync(member, allianceId, ct))
            await conversations.RemoveMemberAsync(group.ConversationId, member, ct);
    }

    public async Task OwnerChangedAsync(ChatGroup group, Guid ownerId, CancellationToken ct)
    {
        if (await db.CyberWarsAlliances.FirstOrDefaultAsync(a => a.Id == group.AppRef, ct) is { } alliance
            && await db.CyberWarsAllianceMembers.AnyAsync(m => m.AllianceId == alliance.Id && m.UserId == ownerId, ct))
        {
            alliance.LeaderId = ownerId;
            await db.SaveChangesAsync(ct);
        }
    }
}
