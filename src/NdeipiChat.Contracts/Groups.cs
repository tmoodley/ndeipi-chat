namespace NdeipiChat.Contracts;

/// <summary>
/// A group chat as a community: who runs it, who's in it and who's online, and the app (if any)
/// that owns it. A CyberWars war room is a group its game owns.
/// </summary>
public static class GroupContract
{
    public const int MaxDescription = 500;
    public const int MaxIcon = 8;
    public const string DefaultIcon = "👥";
    public static readonly TimeSpan MaxMute = TimeSpan.FromDays(7);

    /// <summary>The realtime topic for one group: presence and roster changes, for its members only.</summary>
    public static string Topic(Guid conversationId) => $"group:{conversationId:N}";

    public static string Path(Guid conversationId) => $"api/conversations/{conversationId}/group";
}

public static class GroupKinds
{
    public const string General = "group";
    public const string Alliance = "alliance";
}

/// <summary>A member's role in a group. The owner is one person; admins help run it.</summary>
public static class ChatRoles
{
    public const string Owner = "owner";
    public const string Admin = "admin";
    public const string Member = "member";

    public static int Rank(string? role) => role switch { Owner => 2, Admin => 1, _ => 0 };

    public static bool IsValid(string? role) => role is Owner or Admin or Member;

    public static string Name(string? role) => role switch { Owner => "Owner", Admin => "Admin", _ => "Member" };
}

/// <summary>What each role may do. The server checks every one; the app only uses them to hide buttons.</summary>
public static class ChatPermissions
{
    /// <summary>Rename the group, change its icon and description.</summary>
    public static bool CanEdit(string? role) => ChatRoles.Rank(role) >= 1;

    public static bool CanAdd(string? role) => ChatRoles.Rank(role) >= 1;

    /// <summary>Make or unmake admins, and hand over the group.</summary>
    public static bool CanSetRoles(string? role) => role == ChatRoles.Owner;

    /// <summary>Remove or mute: admins act on members, the owner on anyone else.</summary>
    public static bool CanModerate(string? actor, string? target) =>
        ChatRoles.Rank(actor) >= 1 && ChatRoles.Rank(actor) > ChatRoles.Rank(target);
}

public sealed record GroupFactDto(string Label, string Value);

public sealed record GroupLinkDto(string Label, string Route);

/// <summary>A group app's tab, beside Money, Tokens and History.</summary>
public sealed record GroupAppTabDto(string AppId, string Title, string Icon);

/// <param name="Section">The heading the member is listed under while online (a role, or the app's rank).</param>
/// <param name="SectionOrder">Sections sort by this; offline members are listed last, together.</param>
/// <param name="Rank">A short badge beside the name: "Admin", or the app's rank.</param>
/// <param name="Status">What they're doing, from the group's app ("Raiding Lusaka 2,-1").</param>
/// <param name="Route">Where their profile card's app link goes ("See base").</param>
public sealed record GroupMemberDto(
    Guid UserId, string DisplayName, string? Username, string? AvatarUrl, string Role,
    bool Online, DateTimeOffset? LastSeenAt, DateTimeOffset? MutedUntil, DateTimeOffset JoinedAt,
    string Section, int SectionOrder, string? Rank, string? Status, string? Route);

/// <param name="AppId">The app that owns the group, if any; it decides who joins and leaves.</param>
/// <param name="AppRef">The app's own id for the group (an alliance id), for its tab.</param>
/// <param name="JoinHint">For an app's group: how people join it, since nobody adds them from chat.</param>
public sealed record GroupDto(
    Guid ConversationId, string Title, string? Description, string Icon, string Kind, string? AppId, Guid? AppRef,
    string MyRole, bool CanEdit, bool CanAdd, bool CanSetRoles,
    IReadOnlyList<GroupFactDto> Facts, GroupLinkDto? Link, GroupAppTabDto? Tab, string? JoinHint,
    IReadOnlyList<GroupMemberDto> Members)
{
    public int OnlineCount => Members.Count(m => m.Online);
}

public sealed record SaveGroupInfoRequest(string? Title, string? Description, string? Icon);

public sealed record AddGroupMembersRequest(IReadOnlyList<Guid> UserIds);

public sealed record SetGroupRoleRequest(string Role);

/// <param name="Minutes">How long they can't post; 0 lifts the mute.</param>
public sealed record MuteGroupMemberRequest(int Minutes);

public static class GroupEventKinds
{
    public const string Presence = "presence";

    /// <summary>The roster, a role, a mute or the group's info changed: load the group again.</summary>
    public const string Changed = "changed";
}

/// <summary>What the group:{id} topic carries.</summary>
public sealed record GroupEventDto(string Kind, Guid? UserId = null, bool? Online = null, DateTimeOffset? LastSeenAt = null);
