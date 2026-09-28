using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Social;

/// <summary>
/// The Social app's graph: profiles, who follows whom, finding people, and groups. Posts stay in
/// <see cref="PostService"/>; a post can belong to a group, and a private group's posts are for its
/// members only.
/// </summary>
public sealed class SocialGraphService(ChatDbContext db, TimeProvider clock)
{
    public const int MaxPhotosOnProfile = 12;
    public const int MaxCards = 60;

    // ---- Profiles ----

    public async Task<SocialProfileDto?> ProfileAsync(User me, Guid userId, Uri site, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return null;

        var groups = await GroupsQuery(me.Id).Where(g => g.Members.Any(m => m.UserId == userId) && (!g.IsPrivate || g.Members.Any(m => m.UserId == me.Id)))
            .OrderBy(g => g.Name).Take(12).ToListAsync(ct);
        var photos = await db.PostMedia.AsNoTracking()
            .Join(db.Posts.Where(p => p.AuthorId == userId && (p.GroupId == null || !p.Group!.IsPrivate)), m => m.PostId, p => p.Id, (m, p) => new { m, p.CreatedAt })
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.m.Position)
            .Take(MaxPhotosOnProfile)
            .Select(x => x.m)
            .ToListAsync(ct);
        var gig = await db.GigProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, ct);

        return new SocialProfileDto(
            ChatMapper.ToDto(user),
            user.Bio, user.City, user.Website,
            user.CreatedAt,
            await db.Posts.CountAsync(p => p.AuthorId == userId, ct),
            await db.Follows.CountAsync(f => f.FolloweeId == userId, ct),
            await db.Follows.CountAsync(f => f.FollowerId == userId, ct),
            await db.GroupMembers.CountAsync(m => m.UserId == userId, ct),
            userId == me.Id,
            await db.Follows.AnyAsync(f => f.FollowerId == me.Id && f.FolloweeId == userId, ct),
            await db.Follows.AnyAsync(f => f.FollowerId == userId && f.FolloweeId == me.Id, ct),
            await IsShamwariAsync(me.Id, userId, ct),
            groups.Select(ToSummary).ToList(),
            photos.Select(m => new PostMediaDto(m.Id, m.Width, m.Height, new Uri(site, SocialContract.MediaPath(m.Id, PhotoSizes.Thumb)).ToString())).ToList(),
            gig is null ? null : new GigProfileBriefDto(
                gig.Headline, gig.Skills.Split(',', StringSplitOptions.RemoveEmptyEntries),
                gig.RatingCount == 0 ? null : Math.Round((double)gig.RatingSum / gig.RatingCount, 1), gig.RatingCount, gig.CompletedGigs, gig.IsAvailable));
    }

    public async Task SaveProfileAsync(User me, SaveSocialProfileRequest request, CancellationToken ct)
    {
        me.Bio = Optional(request.Bio, SocialGraphContract.MaxBio, "Your bio");
        me.City = Optional(request.City, 80, "City");
        var website = Optional(request.Website, 200, "Website");
        if (website is not null)
        {
            if (!website.Contains("://"))
                website = "https://" + website;
            if (!Uri.TryCreate(website, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                throw new ChatRejectedException("Enter a web address such as example.com.");
        }
        me.Website = website;
        await db.SaveChangesAsync(ct);
    }

    Task<bool> IsShamwariAsync(Guid me, Guid other, CancellationToken ct) =>
        db.Shamwaris.AnyAsync(l => l.Accepted && ((l.RequesterId == me && l.AddresseeId == other) || (l.RequesterId == other && l.AddresseeId == me)), ct);

    // ---- Follows ----

    public async Task FollowAsync(User me, Guid userId, bool follow, CancellationToken ct)
    {
        if (userId == me.Id)
            throw new ChatRejectedException("You can't follow yourself.");
        if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
            throw new ChatRejectedException("That person doesn't exist.");
        if (!follow)
        {
            await db.Follows.Where(f => f.FollowerId == me.Id && f.FolloweeId == userId).ExecuteDeleteAsync(ct);
            return;
        }
        if (await db.Follows.AnyAsync(f => f.FollowerId == me.Id && f.FolloweeId == userId, ct))
            return;
        db.Follows.Add(new Follow { FollowerId = me.Id, FolloweeId = userId, CreatedAt = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Followed twice at once: followed either way.
            db.ChangeTracker.Clear();
        }
    }

    public Task<List<PersonCardDto>> FollowingAsync(User me, Guid userId, CancellationToken ct) =>
        CardsAsync(me.Id, db.Follows.Where(f => f.FollowerId == userId).OrderByDescending(f => f.CreatedAt).Select(f => f.FolloweeId), ct);

    public Task<List<PersonCardDto>> FollowersAsync(User me, Guid userId, CancellationToken ct) =>
        CardsAsync(me.Id, db.Follows.Where(f => f.FolloweeId == userId).OrderByDescending(f => f.CreatedAt).Select(f => f.FollowerId), ct);

    /// <summary>People to find: matching the search, or else the most active people you don't follow yet.</summary>
    public Task<List<PersonCardDto>> PeopleAsync(User me, string? query, CancellationToken ct)
    {
        var q = query?.Trim();
        var people = db.Users.Where(u => u.Id != me.Id);
        if (!string.IsNullOrEmpty(q))
            people = people.Where(u => u.DisplayName.Contains(q) || (u.Username != null && u.Username.Contains(q)));
        else
            people = people.Where(u => !db.Follows.Any(f => f.FollowerId == me.Id && f.FolloweeId == u.Id));
        return CardsAsync(me.Id, people
            .OrderByDescending(u => db.Posts.Count(p => p.AuthorId == u.Id) + db.Follows.Count(f => f.FolloweeId == u.Id))
            .ThenBy(u => u.DisplayName)
            .Select(u => u.Id), ct);
    }

    async Task<List<PersonCardDto>> CardsAsync(Guid me, IQueryable<Guid> ids, CancellationToken ct)
    {
        var ordered = await ids.Take(MaxCards).ToListAsync(ct);
        var cards = await db.Users.AsNoTracking().Where(u => ordered.Contains(u.Id))
            .Select(u => new
            {
                User = u,
                Posts = db.Posts.Count(p => p.AuthorId == u.Id),
                Followers = db.Follows.Count(f => f.FolloweeId == u.Id),
                IFollow = db.Follows.Any(f => f.FollowerId == me && f.FolloweeId == u.Id),
                Shamwari = db.Shamwaris.Any(l => l.Accepted && ((l.RequesterId == me && l.AddresseeId == u.Id) || (l.RequesterId == u.Id && l.AddresseeId == me)))
            })
            .ToListAsync(ct);
        return cards.OrderBy(c => ordered.IndexOf(c.User.Id))
            .Select(c => new PersonCardDto(ChatMapper.ToDto(c.User), c.User.Bio, c.User.City, c.Posts, c.Followers, c.IFollow, c.Shamwari))
            .ToList();
    }

    // ---- Groups ----

    sealed record GroupRow(SocialGroup Group, int Members, int Posts, string? MyRole);

    IQueryable<SocialGroup> GroupsQuery(Guid me) => db.SocialGroups.AsNoTracking().Include(g => g.Members.Where(m => m.UserId == me));

    IQueryable<GroupRow> Rows(IQueryable<SocialGroup> groups, Guid me) => groups.Select(g => new GroupRow(
        g,
        g.Members.Count,
        db.Posts.Count(p => p.GroupId == g.Id),
        g.Members.Where(m => m.UserId == me).Select(m => m.Role).FirstOrDefault()));

    /// <summary>Your groups, or public groups to discover (matching <paramref name="query"/> if given).</summary>
    public async Task<List<GroupSummaryDto>> GroupsAsync(User me, bool mine, string? query, CancellationToken ct)
    {
        var groups = db.SocialGroups.AsNoTracking();
        groups = mine
            ? groups.Where(g => g.Members.Any(m => m.UserId == me.Id))
            : groups.Where(g => !g.IsPrivate || g.Members.Any(m => m.UserId == me.Id));
        if (query?.Trim() is { Length: > 0 } q)
            groups = groups.Where(g => g.Name.Contains(q) || g.Description.Contains(q));
        var rows = await Rows(groups.OrderByDescending(g => g.Members.Count).ThenBy(g => g.Name).Take(MaxCards), me.Id).ToListAsync(ct);
        return rows.Select(ToSummary).ToList();
    }

    public async Task<GroupDetailDto?> GroupAsync(User me, Guid id, CancellationToken ct)
    {
        var row = await Rows(db.SocialGroups.AsNoTracking().Include(g => g.Owner).Where(g => g.Id == id), me.Id).FirstOrDefaultAsync(ct);
        if (row is null)
            return null;
        var recent = db.GroupMembers.Where(m => m.GroupId == id).OrderByDescending(m => m.JoinedAt).Select(m => m.UserId);
        return new GroupDetailDto(
            ToSummary(row),
            // A private group's rules and members are for its members.
            row.Group.IsPrivate && row.MyRole is null ? "" : row.Group.Rules,
            ChatMapper.ToDto(row.Group.Owner),
            row.Group.CreatedAt,
            row.Group.IsPrivate && row.MyRole is null ? [] : await CardsAsync(me.Id, recent, ct));
    }

    public async Task<GroupDetailDto> CreateGroupAsync(User me, SaveGroupRequest request, CancellationToken ct)
    {
        var group = new SocialGroup { Id = Guid.NewGuid(), Name = "", OwnerId = me.Id, CreatedAt = clock.GetUtcNow() };
        Apply(group, request);
        group.Members.Add(new GroupMember { GroupId = group.Id, UserId = me.Id, Role = GroupRoles.Owner, JoinedAt = group.CreatedAt });
        db.SocialGroups.Add(group);
        await db.SaveChangesAsync(ct);
        return (await GroupAsync(me, group.Id, ct))!;
    }

    public async Task<GroupDetailDto?> UpdateGroupAsync(User me, Guid id, SaveGroupRequest request, CancellationToken ct)
    {
        var group = await db.SocialGroups.FirstOrDefaultAsync(g => g.Id == id && g.OwnerId == me.Id, ct);
        if (group is null)
            return null;
        Apply(group, request);
        await db.SaveChangesAsync(ct);
        return await GroupAsync(me, id, ct);
    }

    static void Apply(SocialGroup group, SaveGroupRequest request)
    {
        var name = request.Name?.Trim() ?? "";
        if (name.Length is < 3 or > SocialGraphContract.MaxGroupName)
            throw new ChatRejectedException($"Give the group a name of 3 to {SocialGraphContract.MaxGroupName} characters.");
        group.Name = name;
        group.Description = Optional(request.Description, SocialGraphContract.MaxGroupDescription, "The description") ?? "";
        group.Rules = Optional(request.Rules, SocialGraphContract.MaxGroupRules, "The rules") ?? "";
        var icon = request.Icon?.Trim();
        group.Icon = string.IsNullOrEmpty(icon) ? "👥" : icon.Length <= 16 ? icon : throw new ChatRejectedException("Pick one emoji for the icon.");
        var tone = request.Tone?.Trim().ToLowerInvariant();
        group.Tone = tone is not null && SocialGraphContract.GroupTones.Contains(tone) ? tone : "blue";
        group.IsPrivate = request.IsPrivate;
    }

    /// <summary>Joins a public group. Private groups are joined by an owner adding you.</summary>
    public async Task<GroupDetailDto?> JoinAsync(User me, Guid id, CancellationToken ct)
    {
        var group = await db.SocialGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null)
            return null;
        if (group.IsPrivate && !await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == me.Id, ct))
            throw new ChatRejectedException("This group is private. Ask its owner to add you.");
        await AddMemberAsync(id, me.Id, ct);
        return await GroupAsync(me, id, ct);
    }

    public async Task<GroupDetailDto?> LeaveAsync(User me, Guid id, CancellationToken ct)
    {
        var member = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == id && m.UserId == me.Id, ct);
        if (member is null)
            return await GroupAsync(me, id, ct);
        if (member.Role == GroupRoles.Owner)
            throw new ChatRejectedException("You own this group, so you can't leave it.");
        db.GroupMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        return await GroupAsync(me, id, ct);
    }

    public async Task<PersonCardDto?> AddMemberByEmailAsync(User me, Guid id, string? email, CancellationToken ct)
    {
        if (!await db.SocialGroups.AnyAsync(g => g.Id == id && g.OwnerId == me.Id, ct))
            return null;
        var normalized = email?.Trim().ToLowerInvariant();
        var user = string.IsNullOrEmpty(normalized) ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.EmailVerified && u.Email == normalized, ct)
            ?? throw new ChatRejectedException("Nobody on Ndeipi has that email address.");
        if (user is null)
            throw new ChatRejectedException("Enter their email address.");
        await AddMemberAsync(id, user.Id, ct);
        return (await CardsAsync(me.Id, db.Users.Where(u => u.Id == user.Id).Select(u => u.Id), ct)).Single();
    }

    public async Task<bool> RemoveMemberAsync(User me, Guid id, Guid userId, CancellationToken ct)
    {
        if (!await db.SocialGroups.AnyAsync(g => g.Id == id && g.OwnerId == me.Id, ct))
            return false;
        if (userId == me.Id)
            throw new ChatRejectedException("You own this group, so you can't remove yourself.");
        await db.GroupMembers.Where(m => m.GroupId == id && m.UserId == userId).ExecuteDeleteAsync(ct);
        return true;
    }

    public async Task<List<PersonCardDto>?> MembersAsync(User me, Guid id, CancellationToken ct)
    {
        var group = await db.SocialGroups.AsNoTracking().FirstOrDefaultAsync(g => g.Id == id, ct);
        if (group is null || (group.IsPrivate && !await db.GroupMembers.AnyAsync(m => m.GroupId == id && m.UserId == me.Id, ct)))
            return null;
        return await CardsAsync(me.Id, db.GroupMembers.Where(m => m.GroupId == id).OrderBy(m => m.JoinedAt).Select(m => m.UserId), ct);
    }

    async Task AddMemberAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        if (await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, ct))
            return;
        db.GroupMembers.Add(new GroupMember { GroupId = groupId, UserId = userId, Role = GroupRoles.Member, JoinedAt = clock.GetUtcNow() });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
        }
    }

    static GroupSummaryDto ToSummary(GroupRow r) => new(
        r.Group.Id, r.Group.Name, r.Group.Description, r.Group.Icon, r.Group.Tone, r.Group.IsPrivate,
        r.Members, r.Posts, r.MyRole is not null, r.MyRole);

    GroupSummaryDto ToSummary(SocialGroup g) => new(
        g.Id, g.Name, g.Description, g.Icon, g.Tone, g.IsPrivate,
        db.GroupMembers.Count(m => m.GroupId == g.Id), db.Posts.Count(p => p.GroupId == g.Id),
        g.Members.Count > 0, g.Members.FirstOrDefault()?.Role);

    static string? Optional(string? value, int max, string what)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= max ? text : throw new ChatRejectedException($"{what} is limited to {max} characters.");
    }
}

public static class SocialGraphModule
{
    public static IServiceCollection AddSocialGraph(this IServiceCollection services) => services.AddScoped<SocialGraphService>();

    public static void MapSocialGraph(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + SocialGraphContract.BasePath).RequireAuthorization().RequireSubApp(BuiltInApps.Feed);

        api.MapGet("/profiles/{userId:guid}", async (Guid userId, HttpContext http, CurrentUserService users, SocialGraphService social, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await social.ProfileAsync(me, userId, posts.SiteFor(http.Request), http.RequestAborted) is { } p ? Results.Ok(p) : Results.NotFound();
        });

        api.MapPut("/profile", async (SaveSocialProfileRequest request, HttpContext http, CurrentUserService users, SocialGraphService social, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            await social.SaveProfileAsync(me, request, http.RequestAborted);
            return Results.Ok(await social.ProfileAsync(me, me.Id, posts.SiteFor(http.Request), http.RequestAborted));
        });

        api.MapPost("/follows/{userId:guid}", async (Guid userId, HttpContext http, CurrentUserService users, SocialGraphService social) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            await social.FollowAsync(me, userId, true, http.RequestAborted);
            return Results.NoContent();
        });

        api.MapDelete("/follows/{userId:guid}", async (Guid userId, HttpContext http, CurrentUserService users, SocialGraphService social) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            await social.FollowAsync(me, userId, false, http.RequestAborted);
            return Results.NoContent();
        });

        api.MapGet("/profiles/{userId:guid}/following", async (Guid userId, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            Results.Ok(await social.FollowingAsync(await users.GetAsync(http.User, http.RequestAborted), userId, http.RequestAborted)));

        api.MapGet("/profiles/{userId:guid}/followers", async (Guid userId, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            Results.Ok(await social.FollowersAsync(await users.GetAsync(http.User, http.RequestAborted), userId, http.RequestAborted)));

        api.MapGet("/people", async (string? q, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            Results.Ok(await social.PeopleAsync(await users.GetAsync(http.User, http.RequestAborted), q, http.RequestAborted)));

        api.MapGet("/groups", async (bool? mine, string? q, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            Results.Ok(await social.GroupsAsync(await users.GetAsync(http.User, http.RequestAborted), mine ?? false, q, http.RequestAborted)));

        api.MapPost("/groups", async (SaveGroupRequest request, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            Results.Ok(await social.CreateGroupAsync(await users.GetAsync(http.User, http.RequestAborted), request, http.RequestAborted)));

        api.MapGet("/groups/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            await social.GroupAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound());

        api.MapPut("/groups/{id:guid}", async (Guid id, SaveGroupRequest request, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            await social.UpdateGroupAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound());

        api.MapPost("/groups/{id:guid}/join", async (Guid id, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            await social.JoinAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound());

        api.MapPost("/groups/{id:guid}/leave", async (Guid id, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            await social.LeaveAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound());

        api.MapGet("/groups/{id:guid}/members", async (Guid id, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            await social.MembersAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) is { } m ? Results.Ok(m) : Results.NotFound());

        api.MapPost("/groups/{id:guid}/members", async (Guid id, AddGroupMemberRequest request, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            await social.AddMemberByEmailAsync(await users.GetAsync(http.User, http.RequestAborted), id, request.Email, http.RequestAborted) is { } m ? Results.Ok(m) : Results.NotFound());

        api.MapDelete("/groups/{id:guid}/members/{userId:guid}", async (Guid id, Guid userId, HttpContext http, CurrentUserService users, SocialGraphService social) =>
            await social.RemoveMemberAsync(await users.GetAsync(http.User, http.RequestAborted), id, userId, http.RequestAborted) ? Results.NoContent() : Results.NotFound());
    }
}
