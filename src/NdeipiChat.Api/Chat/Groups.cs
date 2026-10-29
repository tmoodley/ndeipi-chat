using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Chat;

/// <summary>
/// An app that owns groups, as CyberWars owns its alliances' war rooms. It adds facts to the
/// group's header, ranks and a status line to its members, and a tab beside Money, Tokens and
/// History; and since it decides who's in the group, leaving or removing someone goes through it.
/// </summary>
public interface IGroupApp
{
    string AppId { get; }

    /// <summary>The app's view of one of its groups, or null if what the group belonged to is gone.</summary>
    Task<GroupAppView?> DescribeAsync(ChatGroup group, IReadOnlyList<Guid> memberIds, CancellationToken ct);

    /// <summary>Takes someone out of the app's group and its chat: they left, or an admin removed them.</summary>
    Task RemoveMemberAsync(ChatGroup group, User member, CancellationToken ct);

    /// <summary>The group's owner handed it to someone else.</summary>
    Task OwnerChangedAsync(ChatGroup group, Guid ownerId, CancellationToken ct);
}

public sealed record GroupAppView(
    IReadOnlyList<GroupFactDto> Facts, GroupLinkDto? Link, GroupAppTabDto? Tab, string? JoinHint,
    IReadOnlyDictionary<Guid, GroupAppMember> Members);

/// <summary>Where an app lists a member while they're online, and what it says about them.</summary>
public sealed record GroupAppMember(string Section, int SectionOrder, string? Rank, string? Status, string? Route);

/// <summary>
/// Who has a connection to the chat hub right now. Kept in memory: the API runs as one instance
/// and SignalR has no backplane, so every connection passes through here.
/// </summary>
public sealed class PresenceTracker
{
    readonly ConcurrentDictionary<Guid, int> _connections = new();

    public bool IsOnline(Guid userId) => _connections.TryGetValue(userId, out var count) && count > 0;

    /// <summary>Counts a connection; true if it's their first, so they just came online.</summary>
    public bool Connect(Guid userId) => _connections.AddOrUpdate(userId, 1, (_, count) => count + 1) == 1;

    /// <summary>Drops a connection; true if it was their last, so they just went offline.</summary>
    public bool Disconnect(Guid userId)
    {
        var left = _connections.AddOrUpdate(userId, 0, (_, count) => Math.Max(0, count - 1));
        if (left == 0)
            _connections.TryRemove(new KeyValuePair<Guid, int>(userId, 0));
        return left == 0;
    }
}

/// <summary>Tells someone's groups when they come online or go offline.</summary>
public sealed class GroupPresence(ChatDbContext db, PresenceTracker presence, ChatNotifier notifier, TimeProvider clock)
{
    public async Task ConnectedAsync(User user, CancellationToken ct)
    {
        if (presence.Connect(user.Id))
            await PublishAsync(user.Id, true, user.LastSeenAt, ct);
    }

    public async Task DisconnectedAsync(User user, CancellationToken ct)
    {
        if (!presence.Disconnect(user.Id))
            return;
        var now = clock.GetUtcNow();
        await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now), ct);
        await PublishAsync(user.Id, false, now, ct);
    }

    async Task PublishAsync(Guid userId, bool online, DateTimeOffset? lastSeen, CancellationToken ct)
    {
        var groups = await db.Members.Where(m => m.UserId == userId)
            .Join(db.Conversations.Where(c => c.Type == ConversationType.Group), m => m.ConversationId, c => c.Id, (m, c) => c.Id)
            .ToListAsync(ct);
        foreach (var id in groups)
            await notifier.PublishAsync(GroupContract.Topic(id), new GroupEventDto(GroupEventKinds.Presence, userId, online, lastSeen));
    }
}

/// <summary>group:{conversationId}: its members only.</summary>
public sealed class GroupTopicPolicy(ChatDbContext db) : ITopicPolicy
{
    public string Prefix => "group";

    public async Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Guid.TryParseExact(key, "N", out var id) && await db.Members.AnyAsync(m => m.ConversationId == id && m.UserId == user.Id, ct);
}

/// <summary>
/// A group as a community: its profile, its members with roles and presence, and what its admins
/// can do. Every permission is checked here, whatever the app shows.
/// </summary>
public sealed class GroupService(
    ChatDbContext db,
    ConversationService conversations,
    PresenceTracker presence,
    ChatNotifier notifier,
    IEnumerable<IGroupApp> apps,
    TimeProvider clock)
{
    public async Task<GroupDto> GetAsync(User viewer, Guid conversationId, CancellationToken ct)
    {
        var (conversation, group) = await LoadAsync(viewer, conversationId, ct);
        var me = conversation.Members.Single(m => m.UserId == viewer.Id);
        var view = App(group) is { } app ? await app.DescribeAsync(group, conversation.Members.Select(m => m.UserId).ToList(), ct) : null;
        var now = clock.GetUtcNow();

        var members = conversation.Members
            .Select(m => ToDto(m, view?.Members.GetValueOrDefault(m.UserId), now))
            .OrderBy(m => m.SectionOrder).ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return new GroupDto(
            conversation.Id,
            ChatMapper.ToDto(conversation, viewer.Id, null, 0).Title,
            group.Description,
            group.Icon,
            group.Kind,
            group.AppId,
            group.AppRef,
            me.Role,
            ChatPermissions.CanEdit(me.Role),
            group.AppId is null && ChatPermissions.CanAdd(me.Role),
            ChatPermissions.CanSetRoles(me.Role),
            view?.Facts ?? [],
            view?.Link,
            view?.Tab,
            view?.JoinHint,
            members);
    }

    public async Task<GroupDto> SaveInfoAsync(User actor, Guid conversationId, SaveGroupInfoRequest request, CancellationToken ct)
    {
        var (conversation, group) = await LoadAsync(actor, conversationId, ct);
        if (!ChatPermissions.CanEdit(Role(conversation, actor)))
            throw new ChatRejectedException("Only the group's owner and admins can change its name and description.");

        var title = request.Title?.Trim();
        if (title is not null)
        {
            if (title.Length is 0 or > 100)
                throw new ChatRejectedException("Give the group a name of up to 100 characters.");
            conversation.Title = title;
        }
        if (request.Description is { } description)
        {
            description = description.Trim();
            if (description.Length > GroupContract.MaxDescription)
                throw new ChatRejectedException($"Keep the description under {GroupContract.MaxDescription} characters.");
            group.Description = description.Length == 0 ? null : description;
        }
        if (request.Icon is { } icon)
        {
            icon = icon.Trim();
            if (icon.Length is 0 or > GroupContract.MaxIcon)
                throw new ChatRejectedException("Pick one emoji for the group's icon.");
            group.Icon = icon;
        }
        await db.SaveChangesAsync(ct);

        // Everyone's chat list shows the new name.
        foreach (var member in conversation.Members)
            await notifier.ToUser(member.User.ClerkUserId).ConversationUpdated((await conversations.ListForAsync(member.UserId, conversationId, ct)).Single());
        await ChangedAsync(conversationId);
        return await GetAsync(actor, conversationId, ct);
    }

    public async Task<GroupDto> AddMembersAsync(User actor, Guid conversationId, AddGroupMembersRequest request, CancellationToken ct)
    {
        var (conversation, group) = await LoadAsync(actor, conversationId, ct);
        if (group.AppId is not null)
            throw new ChatRejectedException(App(group) is { } ? "People join this group from its app." : "Nobody can be added to this group.");
        if (!ChatPermissions.CanAdd(Role(conversation, actor)))
            throw new ChatRejectedException("Only the group's owner and admins can add people.");

        var ids = (request.UserIds ?? []).Distinct().Where(id => conversation.Members.All(m => m.UserId != id)).ToList();
        if (ids.Count == 0)
            throw new ChatRejectedException("Pick someone who isn't in the group yet.");
        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync(ct);
        if (users.Count != ids.Count)
            throw new ChatRejectedException("Some of those people don't exist.");
        if (conversation.Members.Count + users.Count > ConversationService.MaxGroupSize)
            throw new ChatRejectedException($"Groups are limited to {ConversationService.MaxGroupSize} people.");
        foreach (var user in users)
            await conversations.AddMemberAsync(conversationId, user, ct);
        return await GetAsync(actor, conversationId, ct);
    }

    /// <summary>
    /// Leaving (yourself) or removing someone else: admins remove members, the owner removes anyone.
    /// In an app's group the app does it, so a war room member leaves the alliance too.
    /// </summary>
    public async Task RemoveAsync(User actor, Guid conversationId, Guid userId, CancellationToken ct)
    {
        var (conversation, group) = await LoadAsync(actor, conversationId, ct);
        var target = conversation.Members.FirstOrDefault(m => m.UserId == userId)
            ?? throw new ChatRejectedException("They aren't in this group.");
        if (userId != actor.Id && !ChatPermissions.CanModerate(Role(conversation, actor), target.Role))
            throw new ChatRejectedException("Only an admin can remove members, and only the owner can remove an admin.");

        if (App(group) is { } app)
        {
            await app.RemoveMemberAsync(group, target.User, ct);
            return;
        }
        await conversations.RemoveMemberAsync(conversationId, target.User, ct);
        if (target.Role == ChatRoles.Owner)
        {
            // The group goes to the longest-serving admin, or else the longest-serving member.
            var next = await db.Members.AsNoTracking().Where(m => m.ConversationId == conversationId)
                .OrderByDescending(m => m.Role == ChatRoles.Admin).ThenBy(m => m.JoinedAt).FirstOrDefaultAsync(ct);
            if (next is not null)
                await conversations.SetRoleAsync(conversationId, next.UserId, ChatRoles.Owner, ct);
        }
    }

    /// <summary>Only the owner sets roles. Making someone the owner hands them the group; the old owner stays as an admin.</summary>
    public async Task<GroupDto> SetRoleAsync(User actor, Guid conversationId, Guid userId, SetGroupRoleRequest request, CancellationToken ct)
    {
        var (conversation, group) = await LoadAsync(actor, conversationId, ct);
        if (!ChatRoles.IsValid(request.Role))
            throw new ChatRejectedException("A role is owner, admin or member.");
        if (!ChatPermissions.CanSetRoles(Role(conversation, actor)))
            throw new ChatRejectedException("Only the group's owner can change roles.");
        if (userId == actor.Id)
            throw new ChatRejectedException("Hand the group to someone else to stop being its owner.");
        if (conversation.Members.All(m => m.UserId != userId))
            throw new ChatRejectedException("They aren't in this group.");

        await conversations.SetRoleAsync(conversationId, userId, request.Role, ct);
        if (request.Role == ChatRoles.Owner && App(group) is { } app)
            await app.OwnerChangedAsync(group, userId, ct);
        db.ChangeTracker.Clear();
        return await GetAsync(actor, conversationId, ct);
    }

    public async Task<GroupDto> MuteAsync(User actor, Guid conversationId, Guid userId, MuteGroupMemberRequest request, CancellationToken ct)
    {
        var (conversation, _) = await LoadAsync(actor, conversationId, ct);
        var target = conversation.Members.FirstOrDefault(m => m.UserId == userId)
            ?? throw new ChatRejectedException("They aren't in this group.");
        if (!ChatPermissions.CanModerate(Role(conversation, actor), target.Role))
            throw new ChatRejectedException("Only an admin can mute members, and only the owner can mute an admin.");
        if (request.Minutes < 0 || request.Minutes > GroupContract.MaxMute.TotalMinutes)
            throw new ChatRejectedException($"Mute someone for up to {GroupContract.MaxMute.TotalDays:0} days.");

        target.MutedUntil = request.Minutes == 0 ? null : clock.GetUtcNow().AddMinutes(request.Minutes);
        await db.SaveChangesAsync(ct);
        await ChangedAsync(conversationId);
        return await GetAsync(actor, conversationId, ct);
    }

    /// <summary>The group with its members, if the user is one. A group made before communities gets its profile and an owner here.</summary>
    async Task<(Conversation Conversation, ChatGroup Group)> LoadAsync(User viewer, Guid conversationId, CancellationToken ct)
    {
        var conversation = await db.Conversations.Include(c => c.Members).ThenInclude(m => m.User).AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null || conversation.Members.All(m => m.UserId != viewer.Id))
            throw new ChatRejectedException("You aren't a member of this conversation.");
        if (conversation.Type != ConversationType.Group)
            throw new ChatRejectedException("Only a group has members to manage.");

        var group = await db.ChatGroups.FirstOrDefaultAsync(g => g.ConversationId == conversationId, ct);
        if (group is null)
            db.ChatGroups.Add(group = new ChatGroup { ConversationId = conversationId, CreatedAt = conversation.CreatedAt });
        if (conversation.Members.All(m => m.Role != ChatRoles.Owner))
            conversation.Members.OrderBy(m => m.JoinedAt).ThenBy(m => m.UserId).First().Role = ChatRoles.Owner;
        if (db.ChangeTracker.HasChanges())
            await db.SaveChangesAsync(ct);
        return (conversation, group);
    }

    IGroupApp? App(ChatGroup group) => group.AppId is null ? null : apps.FirstOrDefault(a => a.AppId == group.AppId);

    static string Role(Conversation conversation, User user) => conversation.Members.Single(m => m.UserId == user.Id).Role;

    GroupMemberDto ToDto(ConversationMember m, GroupAppMember? app, DateTimeOffset now)
    {
        var (section, order) = app is not null
            ? (app.Section, app.SectionOrder)
            : m.Role switch { ChatRoles.Owner => ("Owner", 0), ChatRoles.Admin => ("Admins", 1), _ => ("Members", 2) };
        return new GroupMemberDto(
            m.UserId, m.User.DisplayName, m.User.Username, m.User.AvatarUrl, m.Role,
            presence.IsOnline(m.UserId), m.User.LastSeenAt, m.MutedUntil > now ? m.MutedUntil : null, m.JoinedAt,
            section, order, app?.Rank ?? (m.Role == ChatRoles.Member ? null : ChatRoles.Name(m.Role)), app?.Status, app?.Route);
    }

    Task ChangedAsync(Guid conversationId) =>
        notifier.PublishAsync(GroupContract.Topic(conversationId), new GroupEventDto(GroupEventKinds.Changed));
}

public static class GroupModule
{
    public static IServiceCollection AddGroups(this IServiceCollection services)
    {
        services.AddSingleton<PresenceTracker>();
        services.AddScoped<GroupPresence>();
        services.AddScoped<GroupService>();
        services.AddScoped<ITopicPolicy, GroupTopicPolicy>();
        return services;
    }

    public static void MapGroups(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/conversations/{id:guid}").RequireAuthorization();

        api.MapGet("/group", async (Guid id, HttpContext http, CurrentUserService users, GroupService groups) =>
            Results.Ok(await groups.GetAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapPut("/group", async (Guid id, SaveGroupInfoRequest request, HttpContext http, CurrentUserService users, GroupService groups) =>
            Results.Ok(await groups.SaveInfoAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapPost("/members", async (Guid id, AddGroupMembersRequest request, HttpContext http, CurrentUserService users, GroupService groups) =>
            Results.Ok(await groups.AddMembersAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        // Yourself: leave. Someone else: remove them.
        api.MapDelete("/members/{userId:guid}", async (Guid id, Guid userId, HttpContext http, CurrentUserService users, GroupService groups) =>
        {
            await groups.RemoveAsync(await users.GetAsync(http.User, http.RequestAborted), id, userId, http.RequestAborted);
            return Results.NoContent();
        });

        api.MapPut("/members/{userId:guid}/role", async (Guid id, Guid userId, SetGroupRoleRequest request, HttpContext http, CurrentUserService users, GroupService groups) =>
            Results.Ok(await groups.SetRoleAsync(await users.GetAsync(http.User, http.RequestAborted), id, userId, request, http.RequestAborted)));

        api.MapPut("/members/{userId:guid}/mute", async (Guid id, Guid userId, MuteGroupMemberRequest request, HttpContext http, CurrentUserService users, GroupService groups) =>
            Results.Ok(await groups.MuteAsync(await users.GetAsync(http.User, http.RequestAborted), id, userId, request, http.RequestAborted)));
    }
}
