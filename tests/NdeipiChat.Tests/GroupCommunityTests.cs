using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Group chats as communities: an owner and admins whose powers the server checks, members with
/// presence, mutes, leaving and removal that reach the person in real time; and the CyberWars war
/// room as the first group an app owns, with ranks, a strike vote that's one live card, and its tab.
/// </summary>
public sealed class GroupCommunityTests(TestApp app) : IClassFixture<TestApp>
{
    const string Cw = CyberWarsContract.BasePath;

    static string Unique(string name) => $"{name} {Guid.NewGuid():N}"[..(name.Length + 7)];

    static string GroupPath(Guid id) => GroupContract.Path(id);

    static async Task<string> ErrorOf(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    static async Task<HttpResponseMessage> PutAsync(TestUser user, string path, object body) =>
        await user.Http.PutAsJsonAsync(path, body, ContractJson.Options);

    static async Task<T> PutOkAsync<T>(TestUser user, string path, object body)
    {
        using var response = await PutAsync(user, path, body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    static Task<HttpResponseMessage> SayRawAsync(TestUser user, Guid conversationId, string text) =>
        user.Http.PostAsJsonAsync($"api/conversations/{conversationId}/messages",
            new SendMessageRequest(conversationId, MessageKinds.Text, ContractJson.ToElement(new TextPayload(text)), Guid.NewGuid()), ContractJson.Options);

    static async Task SayAsync(TestUser user, Guid conversationId, string text)
    {
        using var response = await SayRawAsync(user, conversationId, text);
        response.EnsureSuccessStatusCode();
    }

    async Task<(TestUser Owner, TestUser Ann, TestUser Ben, ConversationDto Chat)> GroupAsync()
    {
        var owner = await app.CreateUserAsync(Unique("Chipo Owner"));
        var ann = await app.CreateUserAsync(Unique("Ann Member"));
        var ben = await app.CreateUserAsync(Unique("Ben Member"));
        var chat = await owner.PostAsync<ConversationDto>("api/conversations", new CreateConversationRequest(ConversationType.Group, [ann.Id, ben.Id], "Harare Growers"));
        return (owner, ann, ben, chat);
    }

    [Fact]
    public async Task A_new_group_is_a_community_run_by_whoever_started_it()
    {
        var (owner, ann, _, chat) = await GroupAsync();

        var mine = await owner.GetAsync<GroupDto>(GroupPath(chat.Id));
        Assert.Equal((GroupKinds.General, ChatRoles.Owner, true, true, true), (mine.Kind, mine.MyRole, mine.CanEdit, mine.CanAdd, mine.CanSetRoles));
        Assert.Equal(3, mine.Members.Count);
        var first = mine.Members[0];
        Assert.Equal((owner.Id, "Owner", "Owner"), (first.UserId, first.Section, first.Rank));
        Assert.All(mine.Members.Skip(1), m => Assert.Equal(("Members", null), (m.Section, m.Rank)));

        var theirs = await ann.GetAsync<GroupDto>(GroupPath(chat.Id));
        Assert.Equal((ChatRoles.Member, false, false, false), (theirs.MyRole, theirs.CanEdit, theirs.CanAdd, theirs.CanSetRoles));

        // Only members can see a group, and a direct chat has no members to manage.
        var outsider = await app.CreateUserAsync(Unique("Out Sider"));
        using var peek = await outsider.Http.GetAsync(GroupPath(chat.Id));
        Assert.Equal(HttpStatusCode.BadRequest, peek.StatusCode);
        var direct = await owner.StartDirectChatAsync(ann);
        using var notGroup = await owner.Http.GetAsync(GroupPath(direct.Id));
        Assert.Equal(HttpStatusCode.BadRequest, notGroup.StatusCode);
    }

    [Fact]
    public async Task Only_admins_rename_add_and_remove_and_only_the_owner_sets_roles()
    {
        var (owner, ann, ben, chat) = await GroupAsync();
        var dee = await app.CreateUserAsync(Unique("Dee New"));

        // A member can do none of it.
        using (var rename = await PutAsync(ann, GroupPath(chat.Id), new SaveGroupInfoRequest("Ann's group", null, null)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rename.StatusCode);
            Assert.Contains("owner and admins", await ErrorOf(rename));
        }
        using (var add = await ann.Http.PostAsJsonAsync($"api/conversations/{chat.Id}/members", new AddGroupMembersRequest([dee.Id]), ContractJson.Options))
            Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
        using (var kick = await ann.Http.DeleteAsync($"api/conversations/{chat.Id}/members/{ben.Id}"))
            Assert.Equal(HttpStatusCode.BadRequest, kick.StatusCode);
        using (var promote = await PutAsync(ann, $"api/conversations/{chat.Id}/members/{ann.Id}/role", new SetGroupRoleRequest(ChatRoles.Admin)))
            Assert.Equal(HttpStatusCode.BadRequest, promote.StatusCode);

        // The owner makes Ann an admin; now she can rename, add and remove members, but not the owner.
        var promoted = await PutOkAsync<GroupDto>(owner, $"api/conversations/{chat.Id}/members/{ann.Id}/role", new SetGroupRoleRequest(ChatRoles.Admin));
        Assert.Equal(("Admins", "Admin"), (promoted.Members.Single(m => m.UserId == ann.Id).Section, promoted.Members.Single(m => m.UserId == ann.Id).Rank));

        var renamed = await PutOkAsync<GroupDto>(ann, GroupPath(chat.Id), new SaveGroupInfoRequest("Harare Growers Co-op", "Prices and help for growers.", "🌽"));
        Assert.Equal(("Harare Growers Co-op", "Prices and help for growers.", "🌽"), (renamed.Title, renamed.Description, renamed.Icon));
        Assert.Equal("Harare Growers Co-op", (await ben.GetAsync<ConversationDto>($"api/conversations/{chat.Id}")).Title);

        await using var deeHub = await app.ConnectAsync(dee);
        var appears = Wait.ForEventAsync<ConversationDto>(deeHub, nameof(IChatClient.ConversationUpdated), c => c.Id == chat.Id);
        var added = await ann.PostAsync<GroupDto>($"api/conversations/{chat.Id}/members", new AddGroupMembersRequest([dee.Id]));
        Assert.Contains(added.Members, m => m.UserId == dee.Id && m.Role == ChatRoles.Member);
        await appears;

        using (var kickOwner = await ann.Http.DeleteAsync($"api/conversations/{chat.Id}/members/{owner.Id}"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, kickOwner.StatusCode);
            Assert.Contains("only the owner", await ErrorOf(kickOwner));
        }
        using (var setRole = await PutAsync(ann, $"api/conversations/{chat.Id}/members/{ben.Id}/role", new SetGroupRoleRequest(ChatRoles.Admin)))
            Assert.Equal(HttpStatusCode.BadRequest, setRole.StatusCode);

        // Removing Ben takes the group out of his Chats at once, and he can't post any more.
        await using var benHub = await app.ConnectAsync(ben);
        var removed = Wait.ForEventAsync<Guid>(benHub, nameof(IChatClient.ConversationRemoved), id => id == chat.Id);
        using (var kick = await ann.Http.DeleteAsync($"api/conversations/{chat.Id}/members/{ben.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, kick.StatusCode);
        await removed;
        Assert.DoesNotContain(await ben.GetAsync<List<ConversationDto>>("api/conversations"), c => c.Id == chat.Id);
        using var after = await SayRawAsync(ben, chat.Id, "Hello?");
        Assert.Equal(HttpStatusCode.BadRequest, after.StatusCode);

        // The owner can remove an admin.
        using (var kickAdmin = await owner.Http.DeleteAsync($"api/conversations/{chat.Id}/members/{ann.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, kickAdmin.StatusCode);
    }

    [Fact]
    public async Task A_muted_member_reads_but_cannot_post_until_unmuted()
    {
        var (owner, ann, _, chat) = await GroupAsync();

        var muted = await PutOkAsync<GroupDto>(owner, $"api/conversations/{chat.Id}/members/{ann.Id}/mute", new MuteGroupMemberRequest(60));
        Assert.NotNull(muted.Members.Single(m => m.UserId == ann.Id).MutedUntil);
        using (var blocked = await SayRawAsync(ann, chat.Id, "Can I speak?"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, blocked.StatusCode);
            Assert.Contains("muted", await ErrorOf(blocked));
        }
        await SayAsync(owner, chat.Id, "Ann can still read this.");
        Assert.Single(await ann.GetAsync<List<MessageDto>>($"api/conversations/{chat.Id}/messages"));

        // A member can't mute the owner, and nobody mutes for longer than a week.
        using (var back = await PutAsync(ann, $"api/conversations/{chat.Id}/members/{owner.Id}/mute", new MuteGroupMemberRequest(60)))
            Assert.Equal(HttpStatusCode.BadRequest, back.StatusCode);
        using (var forever = await PutAsync(owner, $"api/conversations/{chat.Id}/members/{ann.Id}/mute", new MuteGroupMemberRequest(60 * 24 * 30)))
            Assert.Equal(HttpStatusCode.BadRequest, forever.StatusCode);

        var unmuted = await PutOkAsync<GroupDto>(owner, $"api/conversations/{chat.Id}/members/{ann.Id}/mute", new MuteGroupMemberRequest(0));
        Assert.Null(unmuted.Members.Single(m => m.UserId == ann.Id).MutedUntil);
        await SayAsync(ann, chat.Id, "Thanks!");
    }

    [Fact]
    public async Task Members_see_each_other_come_online_and_go_offline_and_outsiders_cannot_listen()
    {
        var (owner, ann, _, chat) = await GroupAsync();
        await using var ownerHub = await app.ConnectAsync(owner);
        await ownerHub.InvokeAsync(ChatHubContract.Subscribe, GroupContract.Topic(chat.Id));

        GroupEventDto? Presence(TopicMessageDto m) => m.Topic == GroupContract.Topic(chat.Id) ? ContractJson.Read<GroupEventDto>(m.Payload) : null;
        var online = Wait.ForEventAsync<TopicMessageDto>(ownerHub, nameof(IChatClient.TopicMessage),
            m => Presence(m) is { Kind: GroupEventKinds.Presence, Online: true } e && e.UserId == ann.Id);
        var annHub = await app.ConnectAsync(ann);
        await online;
        var group = await owner.GetAsync<GroupDto>(GroupPath(chat.Id));
        Assert.True(group.Members.Single(m => m.UserId == ann.Id).Online);
        Assert.Equal(2, group.OnlineCount);

        var offline = Wait.ForEventAsync<TopicMessageDto>(ownerHub, nameof(IChatClient.TopicMessage),
            m => Presence(m) is { Kind: GroupEventKinds.Presence, Online: false } e && e.UserId == ann.Id);
        await annHub.DisposeAsync();
        await offline;
        var after = (await owner.GetAsync<GroupDto>(GroupPath(chat.Id))).Members.Single(m => m.UserId == ann.Id);
        Assert.False(after.Online);
        Assert.NotNull(after.LastSeenAt);

        var outsider = await app.CreateUserAsync(Unique("Eaves Dropper"));
        await using var outsiderHub = await app.ConnectAsync(outsider);
        await Assert.ThrowsAsync<HubException>(() => outsiderHub.InvokeAsync(ChatHubContract.Subscribe, GroupContract.Topic(chat.Id)));
    }

    [Fact]
    public async Task When_the_owner_leaves_the_group_goes_to_the_longest_serving_admin()
    {
        var (owner, ann, ben, chat) = await GroupAsync();
        await PutOkAsync<GroupDto>(owner, $"api/conversations/{chat.Id}/members/{ben.Id}/role", new SetGroupRoleRequest(ChatRoles.Admin));

        using (var leave = await owner.Http.DeleteAsync($"api/conversations/{chat.Id}/members/{owner.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, leave.StatusCode);
        var group = await ann.GetAsync<GroupDto>(GroupPath(chat.Id));
        Assert.Equal(2, group.Members.Count);
        Assert.Equal(ChatRoles.Owner, group.Members.Single(m => m.UserId == ben.Id).Role);

        // Handing the group over keeps the old owner as an admin.
        await PutOkAsync<GroupDto>(ben, $"api/conversations/{chat.Id}/members/{ann.Id}/role", new SetGroupRoleRequest(ChatRoles.Owner));
        var handed = await ann.GetAsync<GroupDto>(GroupPath(chat.Id));
        Assert.Equal((ChatRoles.Owner, ChatRoles.Admin), (handed.MyRole, handed.Members.Single(m => m.UserId == ben.Id).Role));
    }

    [Fact]
    public async Task A_group_made_before_communities_gets_a_profile_and_an_owner_when_first_opened()
    {
        var (owner, ann, _, chat) = await GroupAsync();
        await app.DbAsync(async db =>
        {
            await db.ChatGroups.Where(g => g.ConversationId == chat.Id).ExecuteDeleteAsync();
            await db.Members.Where(m => m.ConversationId == chat.Id).ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, ChatRoles.Member));
        });
        var group = await ann.GetAsync<GroupDto>(GroupPath(chat.Id));
        Assert.Equal(GroupKinds.General, group.Kind);
        Assert.Single(group.Members, m => m.Role == ChatRoles.Owner);
    }

    // ---- The war room ----

    async Task<TestUser> PlayerAsync(string name, ZambianProvince province, HexCell sector)
    {
        var user = await app.CreateUserAsync(Unique(name));
        await user.PostAsync<SectorDto>($"{Cw}/home", new ClaimHomeRequest(province, sector));
        return user;
    }

    Task<List<(Guid Id, CyberWarsCardPayload Card, string? State)>> CardsAsync(Guid conversationId) => app.DbAsync(async db =>
        (await db.Messages.Where(m => m.ConversationId == conversationId && m.Kind == MessageKinds.CyberWarsCard).OrderBy(m => m.Seq).ToListAsync())
            .Select(m => (m.Id, ContractJson.Read<CyberWarsCardPayload>(m.PayloadJson)!, m.StateJson)).ToList());

    [Fact]
    public async Task The_war_room_shows_the_alliance_its_ranks_and_its_tab_and_the_game_decides_who_is_in_it()
    {
        var leader = await PlayerAsync("Lion Leader", ZambianProvince.Lusaka, new HexCell(1, -1));
        var recruit = await PlayerAsync("Lion Recruit", ZambianProvince.Lusaka, new HexCell(0, 1));
        var alliance = await leader.PostAsync<AllianceDto>($"{Cw}/alliances", new CreateAllianceRequest(Unique("Lusaka Lions")));
        await recruit.PostAsync<AllianceDto>($"{Cw}/alliances/{alliance.Id}/join", new { });

        var room = await leader.GetAsync<GroupDto>(GroupPath(alliance.ConversationId));
        Assert.Equal((GroupKinds.Alliance, CyberWarsContract.AppId, alliance.Id, "⚔️"), (room.Kind, room.AppId, room.AppRef!.Value, room.Icon));
        Assert.Equal(ChatRoles.Owner, room.MyRole);
        Assert.False(room.CanAdd);
        Assert.Contains(room.Facts, f => f is { Label: "Province", Value: "Lusaka" });
        Assert.Contains(room.Facts, f => f is { Label: "Home sector", Value: "Lusaka 1,-1" });
        Assert.Equal(("War room", "apps/cyberwars?sector=Lusaka:1:-1"), (room.Tab!.Title, room.Link!.Route));
        var top = room.Members[0];
        Assert.Equal((leader.Id, "Leader", "Leader"), (top.UserId, top.Section, top.Rank));
        Assert.Equal("Recruit", room.Members.Single(m => m.UserId == recruit.Id).Rank);

        // People join from the game, not from chat.
        var friend = await app.CreateUserAsync(Unique("Chat Friend"));
        using (var add = await leader.Http.PostAsJsonAsync($"api/conversations/{alliance.ConversationId}/members", new AddGroupMembersRequest([friend.Id]), ContractJson.Options))
        {
            Assert.Equal(HttpStatusCode.BadRequest, add.StatusCode);
            Assert.Contains("from its app", await ErrorOf(add));
        }

        // Handing over the room makes a new alliance leader; removing someone takes them out of the alliance.
        await PutOkAsync<GroupDto>(leader, $"api/conversations/{alliance.ConversationId}/members/{recruit.Id}/role", new SetGroupRoleRequest(ChatRoles.Owner));
        Assert.True((await recruit.GetAsync<CyberWarsMeDto>($"{Cw}/me")).Alliance!.Members.Single(m => m.UserId == recruit.Id).Leader);
        using (var kick = await recruit.Http.DeleteAsync($"api/conversations/{alliance.ConversationId}/members/{leader.Id}"))
            Assert.Equal(HttpStatusCode.NoContent, kick.StatusCode);
        Assert.Null((await leader.GetAsync<CyberWarsMeDto>($"{Cw}/me")).Alliance);
        Assert.Single((await recruit.GetAsync<CyberWarsMeDto>($"{Cw}/me")).Alliance!.Members);
    }

    [Fact]
    public async Task A_strike_vote_is_one_card_that_members_vote_on_with_a_tap()
    {
        var leader = await PlayerAsync("Strike Leader", ZambianProvince.Copperbelt, new HexCell(0, 0));
        var second = await PlayerAsync("Strike Second", ZambianProvince.Copperbelt, new HexCell(1, 0));
        var third = await PlayerAsync("Strike Third", ZambianProvince.Copperbelt, new HexCell(0, 1));
        var alliance = await leader.PostAsync<AllianceDto>($"{Cw}/alliances", new CreateAllianceRequest(Unique("Kitwe Copper")));
        await second.PostAsync<AllianceDto>($"{Cw}/alliances/{alliance.Id}/join", new { });
        await third.PostAsync<AllianceDto>($"{Cw}/alliances/{alliance.Id}/join", new { });

        await SayAsync(leader, alliance.ConversationId, "/vote-strike Copperbelt 2,-1");
        var (cardId, card, state) = Assert.Single(await CardsAsync(alliance.ConversationId));
        Assert.Equal((CyberWarsCardKinds.Strike, alliance.Id, new HexCell(2, -1)), (card.Kind, card.AllianceId!.Value, card.Sector!.Value));
        var opened = ContractJson.Read<StrikeCardState>(state!)!;
        Assert.Equal((1, 2, false), (opened.Votes, opened.Needed, opened.Closed));
        Assert.Equal([leader.Id], opened.VoterIds);

        // The second vote, from the card's button, updates that card for everyone and approves the strike.
        await using var hub = await app.ConnectAsync(third);
        var updated = Wait.ForEventAsync<MessageStateDto>(hub, nameof(IChatClient.MessageStateChanged), s => s.MessageId == cardId);
        var strike = await second.PostAsync<StrikeDto>($"{Cw}/alliances/{alliance.Id}/strike/vote", new { });
        Assert.Equal(2, strike.Votes);
        Assert.NotNull(strike.ApprovedUntil);
        var live = ContractJson.Read<StrikeCardState>((await updated).State)!;
        Assert.Equal(2, live.Votes);
        Assert.NotNull(live.ApprovedUntil);

        // A second tap counts once; the approval is announced once.
        var again = await second.PostAsync<StrikeDto>($"{Cw}/alliances/{alliance.Id}/strike/vote", new { });
        Assert.Equal(2, again.Votes);
        var cards = await CardsAsync(alliance.ConversationId);
        Assert.Equal(2, cards.Count);
        Assert.Contains("Strike approved", cards[1].Card.Title);

        // Outsiders can't vote, and a new vote closes the old card.
        var outsider = await PlayerAsync("Strike Outsider", ZambianProvince.Copperbelt, new HexCell(-1, 0));
        using (var foreign = await outsider.Http.PostAsync($"{Cw}/alliances/{alliance.Id}/strike/vote", null))
            Assert.Equal(HttpStatusCode.BadRequest, foreign.StatusCode);
        await SayAsync(third, alliance.ConversationId, "/vote-strike Copperbelt 0,2");
        var closed = ContractJson.Read<StrikeCardState>((await CardsAsync(alliance.ConversationId)).Single(c => c.Id == cardId).State!)!;
        Assert.True(closed.Closed);
    }

    [Fact]
    public async Task The_war_room_tab_shows_pings_and_ways_into_the_game_to_members_only()
    {
        var scout = await PlayerAsync("Tab Scout", ZambianProvince.Eastern, new HexCell(0, 0));
        var alliance = await scout.PostAsync<AllianceDto>($"{Cw}/alliances", new CreateAllianceRequest(Unique("Chipata Eyes")));
        (await scout.Http.PostAsJsonAsync($"{Cw}/pings", new AlliancePingRequest(new HexCell(1, -1), "Towers down", ZambianProvince.Eastern), ContractJson.Options)).EnsureSuccessStatusCode();

        var ping = Assert.Single(await CardsAsync(alliance.ConversationId)).Card;
        Assert.Equal((CyberWarsCardKinds.Ping, ZambianProvince.Eastern, new HexCell(1, -1)), (ping.Kind, ping.Province!.Value, ping.Sector!.Value));

        var room = await scout.GetAsync<WarRoomDto>($"{Cw}/alliances/{alliance.Id}/room");
        Assert.Equal(("Eastern 0,0", "apps/cyberwars?sector=Eastern:0:0", "Eastern 0,0"), (room.HomeSector, room.MapRoute, room.MyBaseSector));
        var shown = Assert.Single(room.Pings);
        Assert.Equal(("Eastern 1,-1", "Towers down", scout.Name), (shown.Place, shown.Text, shown.From));
        Assert.Equal(1, room.SectorsHeld);

        var outsider = await app.CreateUserAsync(Unique("Tab Outsider"));
        using var peek = await outsider.Http.GetAsync($"{Cw}/alliances/{alliance.Id}/room");
        Assert.Equal(HttpStatusCode.BadRequest, peek.StatusCode);
    }
}
