using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Creators;

/// <summary>
/// Creators (NDEIPI-SRS-CREATOR-001): profiles and storefronts, tiers, posts and who may see them,
/// and the creator's studio (subscribers, broadcasts, analytics). Paying is <see cref="CreatorBilling"/>'s.
/// </summary>
public sealed class CreatorService(
    ChatDbContext db,
    CreatorMediaService media,
    ConversationService conversations,
    MessageService messages,
    IOptions<CreatorsOptions> options,
    TimeProvider clock)
{
    DateTimeOffset Now => clock.GetUtcNow();
    DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    // ---- Who may see what (FR-CR-05, FR-CR-09) ----

    /// <summary>The viewer's subscription to a creator that still gives access, if any.</summary>
    internal Task<CreatorSubscription?> AccessAsync(Guid viewerId, Guid creatorId, CancellationToken ct) =>
        db.CreatorSubscriptions.AsNoTracking()
            .Where(s => s.SubscriberId == viewerId && s.CreatorId == creatorId && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace))
            .OrderByDescending(s => s.StartedAt).FirstOrDefaultAsync(ct);

    /// <summary>Whether the viewer may see a post in full: its creator; anyone once it's public; subscribers to its tier or above; buyers.</summary>
    async Task<(bool Visible, bool Unlocked)> CanSeeAsync(Guid viewerId, CreatorPost post, CreatorSubscription? subscription, Dictionary<Guid, int> ranks, CancellationToken ct)
    {
        if (post.CreatorId == viewerId)
            return (true, false);
        if (post.Access == PostAccess.Public)
            return (true, false);
        if (post.TierId is { } tier && subscription is not null
            && ranks.GetValueOrDefault(subscription.TierId) >= ranks.GetValueOrDefault(tier, int.MaxValue))
            return (true, false);
        if (post.Access == PostAccess.PayPerView && await db.CreatorUnlocks.AnyAsync(u => u.PostId == post.Id && u.UserId == viewerId, ct))
            return (true, true);
        return (false, false);
    }

    async Task<Dictionary<Guid, int>> RanksAsync(Guid creatorId, CancellationToken ct) =>
        await db.CreatorTiers.AsNoTracking().Where(t => t.CreatorId == creatorId).ToDictionaryAsync(t => t.Id, t => t.Rank, ct);

    // ---- Me and discovering creators ----

    public async Task<CreatorsMeDto> MeAsync(User user, CancellationToken ct)
    {
        var isCreator = await db.CreatorProfiles.AnyAsync(c => c.UserId == user.Id, ct);
        var canApply = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => p.KycStatus).FirstOrDefaultAsync(ct) == BankingProfile.Approved;
        var subs = await db.CreatorSubscriptions.AsNoTracking().Where(s => s.SubscriberId == user.Id).OrderByDescending(s => s.StartedAt).Take(100).ToListAsync(ct);
        return new CreatorsMeDto(isCreator, canApply, options.Value.FeePercent, await SubscriptionDtosAsync(subs, ct));
    }

    public async Task<IReadOnlyList<CreatorCardDto>> DiscoverAsync(string? q, string? category, CancellationToken ct)
    {
        var query = db.CreatorProfiles.AsNoTracking().AsQueryable();
        if (!string.IsNullOrEmpty(category))
            query = query.Where(c => c.Category == category);
        if (q?.Trim() is { Length: > 0 } text)
            query = query.Where(c => c.Name.Contains(text) || (c.Bio != null && c.Bio.Contains(text)));
        var creators = await query.OrderByDescending(c => db.CreatorSubscriptions.Count(s => s.CreatorId == c.UserId && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace)))
            .ThenByDescending(c => c.UpdatedAt).Take(60)
            .Select(c => new
            {
                c.UserId, c.Name, c.Category, c.BannerMediaId,
                Avatar = db.Users.Where(u => u.Id == c.UserId).Select(u => u.AvatarUrl).FirstOrDefault(),
                From = db.CreatorTiers.Where(t => t.CreatorId == c.UserId && t.Active).Min(t => (decimal?)t.MonthlyPrice),
                Subs = db.CreatorSubscriptions.Count(s => s.CreatorId == c.UserId && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace))
            }).ToListAsync(ct);
        return creators.Select(c => new CreatorCardDto(c.UserId, c.Name, c.Category, c.Avatar,
            c.BannerMediaId is { } b ? media.Link(b, CreatorMediaService.Thumb) : null, c.From, c.Subs)).ToList();
    }

    /// <summary>A storefront (FR-CR-03). Counts as a visit for the funnel (FR-CR-16).</summary>
    public async Task<CreatorDto?> StorefrontAsync(User viewer, Guid creatorId, CancellationToken ct)
    {
        var profile = await db.CreatorProfiles.AsNoTracking().FirstOrDefaultAsync(c => c.UserId == creatorId, ct);
        if (profile is null)
            return null;
        if (creatorId != viewer.Id)
            await RecordAsync(creatorId, viewer.Id, Guid.Empty, CreatorActivity.Visit, ct);
        return await ToDtoAsync(viewer, profile, ct);
    }

    async Task<CreatorDto> ToDtoAsync(User viewer, CreatorProfile profile, CancellationToken ct)
    {
        var mine = profile.UserId == viewer.Id;
        var tiers = await db.CreatorTiers.AsNoTracking().Where(t => t.CreatorId == profile.UserId && (t.Active || mine))
            .OrderBy(t => t.Rank).ToListAsync(ct);
        var counts = await db.CreatorSubscriptions.AsNoTracking()
            .Where(s => s.CreatorId == profile.UserId && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace))
            .GroupBy(s => s.TierId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var posts = await db.CreatorPosts.CountAsync(p => p.CreatorId == profile.UserId && !p.Deleted && p.PublishAt <= Now, ct);
        var avatar = await db.Users.Where(u => u.Id == profile.UserId).Select(u => u.AvatarUrl).FirstOrDefaultAsync(ct);
        var mySub = mine ? null : await AccessAsync(viewer.Id, profile.UserId, ct);
        return new CreatorDto(
            profile.UserId, profile.Name, profile.Category, profile.Bio, avatar,
            profile.BannerMediaId is { } b ? media.Link(b, CreatorMediaService.Full) : null,
            JsonSerializer.Deserialize<List<CreatorLink>>(profile.LinksJson, ContractJson.Options) ?? [],
            profile.PinnedPostId,
            tiers.Select(t => ToDto(t, counts.GetValueOrDefault(t.Id))).ToList(),
            counts.Values.Sum(), posts, mine,
            mySub is null ? null : (await SubscriptionDtosAsync([mySub], ct))[0]);
    }

    static CreatorTierDto ToDto(CreatorTier t, int subscribers) =>
        new(t.Id, t.Name, t.Description, t.MonthlyPrice, t.AnnualPrice, t.Rank, t.Active, subscribers);

    // ---- Becoming a creator and the storefront (FR-CR-01..03) ----

    public async Task<CreatorDto> SaveProfileAsync(User user, SaveCreatorRequest r, CancellationToken ct)
    {
        var profile = await db.CreatorProfiles.FirstOrDefaultAsync(c => c.UserId == user.Id, ct);
        if (profile is null)
        {
            // FR-CR-02: the identity check comes first (Bridge KYC, as for the wallet).
            var kyc = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => p.KycStatus).FirstOrDefaultAsync(ct);
            if (kyc != BankingProfile.Approved)
                throw new ChatRejectedException("Verify your identity under Me > Wallet first: creators are paid into their Ndeipi wallet.");
            profile = new CreatorProfile { UserId = user.Id, Name = "", Category = "other", CreatedAt = Now };
            db.CreatorProfiles.Add(profile);
        }
        profile.Name = Required(r.Name, CreatorsContract.MaxNameLength, "Give your creator page a name.");
        if (!CreatorsContract.Categories.Any(c => c.Code == r.Category))
            throw new ChatRejectedException("Choose a category.");
        profile.Category = r.Category;
        profile.Bio = Optional(r.Bio, CreatorsContract.MaxBioLength);
        var links = (r.Links ?? []).Take(8).Select(l => new CreatorLink(Required(l.Label, 40, "Name each link."), SafeUrl(l.Url))).ToList();
        profile.LinksJson = JsonSerializer.Serialize(links, ContractJson.Options);
        if (r.PinnedPostId is { } pin && !await db.CreatorPosts.AnyAsync(p => p.Id == pin && p.CreatorId == user.Id && !p.Deleted, ct))
            throw new ChatRejectedException("Pin one of your own posts.");
        profile.PinnedPostId = r.PinnedPostId;
        profile.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(user, profile, ct);
    }

    public async Task<CreatorDto> SetBannerAsync(User user, IFormFile file, CancellationToken ct)
    {
        var profile = await MyProfileAsync(user, ct);
        var uploaded = await media.UploadAsync(user.Id, file, ct);
        if (uploaded.Type != MediaTypes.Image)
            throw new ChatRejectedException("The banner has to be a photo.");
        profile.BannerMediaId = uploaded.Id;
        profile.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(user, profile, ct);
    }

    async Task<CreatorProfile> MyProfileAsync(User user, CancellationToken ct) =>
        await db.CreatorProfiles.FirstOrDefaultAsync(c => c.UserId == user.Id, ct)
        ?? throw new ChatRejectedException("Set up your creator page first.");

    // ---- Tiers (FR-CR-04) ----

    public async Task<IReadOnlyList<CreatorTierDto>> TiersAsync(User user, CancellationToken ct)
    {
        await MyProfileAsync(user, ct);
        return (await ToDtoAsync(user, (await db.CreatorProfiles.AsNoTracking().FirstAsync(c => c.UserId == user.Id, ct)), ct)).Tiers;
    }

    public async Task<CreatorTierDto?> SaveTierAsync(User user, Guid? id, SaveCreatorTierRequest r, CancellationToken ct)
    {
        await MyProfileAsync(user, ct);
        var name = Required(r.Name, 40, "Name the tier, e.g. Bronze.");
        if (r.MonthlyPrice is < 0.5m or > 10_000m || Math.Round(r.MonthlyPrice, 2) != r.MonthlyPrice)
            throw new ChatRejectedException("Set a monthly price between $0.50 and $10,000, to the cent.");
        if (r.AnnualPrice is { } annual && (annual < 0.5m || annual > 100_000m || Math.Round(annual, 2) != annual))
            throw new ChatRejectedException("Set the annual price to the cent, or leave it empty.");
        if (r.Rank is < 1 or > CreatorsContract.MaxTiers)
            throw new ChatRejectedException($"Rank it from 1 to {CreatorsContract.MaxTiers}.");

        CreatorTier? tier;
        if (id is { } existing)
        {
            tier = await db.CreatorTiers.FirstOrDefaultAsync(t => t.Id == existing && t.CreatorId == user.Id, ct);
            if (tier is null)
                return null;
        }
        else
        {
            tier = new CreatorTier { Id = Guid.NewGuid(), CreatorId = user.Id, Name = name, CreatedAt = Now };
            db.CreatorTiers.Add(tier);
        }
        var others = await db.CreatorTiers.Where(t => t.CreatorId == user.Id && t.Active && t.Id != tier.Id).ToListAsync(ct);
        if (r.Active && others.Count >= CreatorsContract.MaxTiers)
            throw new ChatRejectedException($"You can offer up to {CreatorsContract.MaxTiers} tiers at once.");
        if (r.Active && others.Any(t => t.Rank == r.Rank))
            throw new ChatRejectedException("Another tier already has that rank.");
        (tier.Name, tier.Description, tier.MonthlyPrice, tier.AnnualPrice, tier.Rank, tier.Active) =
            (name, Optional(r.Description, 500), r.MonthlyPrice, r.AnnualPrice, r.Rank, r.Active);
        await db.SaveChangesAsync(ct);
        return ToDto(tier, await db.CreatorSubscriptions.CountAsync(s => s.TierId == tier.Id && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace), ct));
    }

    // ---- Posts (FR-CR-05, 08, 09, 10) ----

    public async Task<CreatorMediaDto> UploadAsync(User user, IFormFile file, CancellationToken ct)
    {
        await MyProfileAsync(user, ct);
        return media.ToDto(await media.UploadAsync(user.Id, file, ct), withLinks: true);
    }

    public async Task<CreatorPostDto?> SavePostAsync(User user, Guid? id, SaveCreatorPostRequest r, CancellationToken ct)
    {
        await MyProfileAsync(user, ct);
        var title = Required(r.Title, CreatorsContract.MaxTitleLength, "Give the post a title.");
        var body = Optional(r.Body, CreatorsContract.MaxBodyLength);
        if (r.Access is not (PostAccess.Public or PostAccess.Tier or PostAccess.PayPerView))
            throw new ChatRejectedException("Choose who can see it: everyone, a tier, or pay-per-view.");
        if (r.TierId is { } tierId && !await db.CreatorTiers.AnyAsync(t => t.Id == tierId && t.CreatorId == user.Id, ct))
            throw new ChatRejectedException("Choose one of your tiers.");
        if (r.Access == PostAccess.Tier && r.TierId is null)
            throw new ChatRejectedException("Choose the tier it's for.");
        if (r.Access == PostAccess.PayPerView && (r.Price is not ({ } p and >= 0.5m and <= 10_000m) || Math.Round(p, 2) != p))
            throw new ChatRejectedException("Set a pay-per-view price between $0.50 and $10,000, to the cent.");
        var mediaIds = (r.MediaIds ?? []).Distinct().ToList();
        if (mediaIds.Count > CreatorsContract.MaxMediaPerPost)
            throw new ChatRejectedException($"Add up to {CreatorsContract.MaxMediaPerPost} photos, audio or videos.");
        if (await db.CreatorMedia.CountAsync(m => mediaIds.Contains(m.Id) && m.CreatorId == user.Id, ct) != mediaIds.Count)
            throw new ChatRejectedException("A photo, audio or video is missing. Please add it again.");

        CreatorPost? post;
        if (id is { } existing)
        {
            post = await db.CreatorPosts.FirstOrDefaultAsync(x => x.Id == existing && x.CreatorId == user.Id && !x.Deleted, ct);
            if (post is null)
                return null;
        }
        else
        {
            post = new CreatorPost { Id = Guid.NewGuid(), CreatorId = user.Id, Title = title, Access = r.Access, CreatedAt = Now };
            db.CreatorPosts.Add(post);
        }
        post.Title = title;
        post.Body = body;
        post.Teaser = Optional(r.Teaser, CreatorsContract.MaxTeaserLength);
        post.MediaIds = string.Join(',', mediaIds.Select(m => m.ToString("N")));
        post.Access = r.Access;
        post.TierId = r.Access == PostAccess.Public ? null : r.TierId;
        post.Price = r.Access == PostAccess.PayPerView ? r.Price : null;
        // FR-CR-10: a time in the future schedules it; nothing (or the past) publishes now.
        post.PublishAt = r.PublishAt is { } at && at > Now ? at : id is null ? Now : (post.PublishAt > Now ? Now : post.PublishAt);
        post.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return await PostDtoAsync(user, post, ct);
    }

    public async Task<bool> DeletePostAsync(User user, Guid id, CancellationToken ct) =>
        await db.CreatorPosts.Where(p => p.Id == id && p.CreatorId == user.Id && !p.Deleted)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Deleted, true).SetProperty(p => p.UpdatedAt, Now), ct) > 0;

    /// <summary>A creator's posts, newest first: published ones, plus scheduled ones for the creator themself.</summary>
    public async Task<CreatorPostPageDto?> PostsAsync(User viewer, Guid creatorId, DateTimeOffset? before, CancellationToken ct)
    {
        if (!await db.CreatorProfiles.AnyAsync(c => c.UserId == creatorId, ct))
            return null;
        var mine = creatorId == viewer.Id;
        var query = db.CreatorPosts.AsNoTracking().Where(p => p.CreatorId == creatorId && !p.Deleted && (mine || p.PublishAt <= Now));
        if (before is { } b)
            query = query.Where(p => p.PublishAt < b);
        var page = await query.OrderByDescending(p => p.PublishAt).Take(20).ToListAsync(ct);
        var result = new List<CreatorPostDto>();
        foreach (var post in page)
            result.Add(await PostDtoAsync(viewer, post, ct, countView: false));
        return new CreatorPostPageDto(result, page.Count == 20 ? page[^1].PublishAt : null);
    }

    /// <summary>One post; opening one you may see counts a view (FR-CR-16).</summary>
    public async Task<CreatorPostDto?> PostAsync(User viewer, Guid postId, CancellationToken ct)
    {
        var post = await db.CreatorPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId && !p.Deleted, ct);
        if (post is null || (post.PublishAt > Now && post.CreatorId != viewer.Id))
            return null;
        return await PostDtoAsync(viewer, post, ct, countView: true);
    }

    public async Task PlayedAsync(User viewer, Guid postId, CancellationToken ct)
    {
        var post = await db.CreatorPosts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == postId && !p.Deleted, ct);
        if (post is not null && post.CreatorId != viewer.Id)
            await RecordAsync(post.CreatorId, viewer.Id, post.Id, CreatorActivity.Play, ct);
    }

    internal async Task<CreatorPostDto> PostDtoAsync(User viewer, CreatorPost post, CancellationToken ct, bool countView = false)
    {
        var ranks = await RanksAsync(post.CreatorId, ct);
        var subscription = post.CreatorId == viewer.Id ? null : await AccessAsync(viewer.Id, post.CreatorId, ct);
        var (visible, unlocked) = await CanSeeAsync(viewer.Id, post, subscription, ranks, ct);
        if (visible && countView && post.CreatorId != viewer.Id)
            await RecordAsync(post.CreatorId, viewer.Id, post.Id, CreatorActivity.View, ct);

        var ids = post.MediaIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => Guid.ParseExact(i, "N")).ToList();
        var rows = await db.CreatorMedia.AsNoTracking().Where(m => ids.Contains(m.Id)).ToListAsync(ct);
        var items = ids.Select(i => rows.FirstOrDefault(r => r.Id == i)).OfType<CreatorMedia>().Select(m => media.ToDto(m, withLinks: visible)).ToList();
        var creator = await db.CreatorProfiles.AsNoTracking().Where(c => c.UserId == post.CreatorId).Select(c => c.Name).FirstAsync(ct);
        var tierName = post.TierId is { } t ? await db.CreatorTiers.Where(x => x.Id == t).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        int? views = post.CreatorId == viewer.Id
            ? await db.CreatorActivity.CountAsync(a => a.PostId == post.Id && a.Kind == CreatorActivity.View, ct)
            : null;
        return new CreatorPostDto(post.Id, post.CreatorId, creator, post.Title,
            post.Teaser ?? Teaser(post.Body), visible ? post.Body : null, items, post.Access, post.TierId, tierName, post.Price,
            Locked: !visible, Unlocked: unlocked, IsScheduled: post.PublishAt > Now, post.PublishAt, views);
    }

    static string? Teaser(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;
        var flat = string.Join(' ', body.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return flat.Length <= 160 ? flat : flat[..157].TrimEnd() + "…";
    }

    /// <summary>Counted once a day per person (and post), so repeats don't inflate the numbers.</summary>
    async Task RecordAsync(Guid creatorId, Guid userId, Guid postId, string kind, CancellationToken ct)
    {
        var day = Today;
        if (await db.CreatorActivity.AnyAsync(a => a.CreatorId == creatorId && a.UserId == userId && a.PostId == postId && a.Kind == kind && a.Day == day, ct))
            return;
        db.CreatorActivity.Add(new CreatorActivity { CreatorId = creatorId, UserId = userId, PostId = postId, Kind = kind, Day = day });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Counted already by a request alongside this one.
            db.ChangeTracker.Clear();
        }
    }

    // ---- The studio: subscribers, broadcasts, analytics (FR-CR-14..16) ----

    public async Task<IReadOnlyList<SubscriberDto>> SubscribersAsync(User user, CancellationToken ct)
    {
        await MyProfileAsync(user, ct);
        var subs = await db.CreatorSubscriptions.AsNoTracking()
            .Where(s => s.CreatorId == user.Id && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace))
            .OrderBy(s => s.StartedAt).ToListAsync(ct);
        var tiers = await db.CreatorTiers.AsNoTracking().Where(t => t.CreatorId == user.Id).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var sharing = subs.Where(s => s.ShareProfile).Select(s => s.SubscriberId).ToList();
        var names = await db.Users.AsNoTracking().Where(u => sharing.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        return subs.Select(s => new SubscriberDto(
            s.ShareProfile && names.TryGetValue(s.SubscriberId, out var name) ? name : $"Subscriber #{s.Id.ToString("N")[..4].ToUpperInvariant()}",
            !s.ShareProfile,
            tiers.GetValueOrDefault(s.TierId, ""),
            s.Period,
            s.Status,
            s.StartedAt,
            Math.Max(1, (int)((Now - s.StartedAt).TotalDays / 30.44) + 1))).ToList();
    }

    /// <summary>
    /// FR-CR-15: an announcement to every active subscriber (or one tier and up), as a message from the
    /// creator in their direct chat, so it lands in their inbox.
    /// </summary>
    public async Task<BroadcastResultDto> BroadcastAsync(User user, CreatorBroadcastRequest r, CancellationToken ct)
    {
        var profile = await MyProfileAsync(user, ct);
        var text = Required(r.Text, CreatorsContract.MaxBroadcastLength, "Write the announcement.");
        var ranks = await RanksAsync(user.Id, ct);
        var minimum = r.TierId is { } tier ? ranks.GetValueOrDefault(tier, int.MaxValue) : 0;
        var subscribers = (await db.CreatorSubscriptions.AsNoTracking()
                .Where(s => s.CreatorId == user.Id && (s.Status == SubscriptionStatuses.Active || s.Status == SubscriptionStatuses.Grace))
                .Select(s => new { s.SubscriberId, s.TierId }).ToListAsync(ct))
            .Where(s => ranks.GetValueOrDefault(s.TierId) >= minimum).Select(s => s.SubscriberId).Distinct().ToList();

        var broadcastId = Guid.NewGuid();
        var sent = 0;
        foreach (var subscriberId in subscribers)
        {
            var chat = await conversations.CreateAsync(user, new CreateConversationRequest(ConversationType.Direct, [subscriberId], null), ct);
            await messages.SendAsync(user, new SendMessageRequest(chat.Id, MessageKinds.Text,
                ContractJson.ToElement(new TextPayload($"📣 {profile.Name}: {text}")), Deterministic(broadcastId, subscriberId)), ct);
            sent++;
        }
        return new BroadcastResultDto(sent);
    }

    public async Task<CreatorAnalyticsDto> AnalyticsAsync(User user, CancellationToken ct)
    {
        await MyProfileAsync(user, ct);
        var since = DateOnly.FromDateTime(Now.AddDays(-30).UtcDateTime);
        var activity = await db.CreatorActivity.AsNoTracking().Where(a => a.CreatorId == user.Id && a.Day >= since).ToListAsync(ct);
        var visitors = activity.Where(a => a.Kind == CreatorActivity.Visit).Select(a => a.UserId).Distinct().Count();
        var start = Now.AddDays(-30);
        var newSubs = await db.CreatorSubscriptions.CountAsync(s => s.CreatorId == user.Id && s.StartedAt >= start, ct);
        var unlocks = await db.CreatorPayments.AsNoTracking()
            .Where(p => p.CreatorId == user.Id && p.Kind == CreatorPaymentKinds.Unlock && p.Status == TransferStatuses.Confirmed && p.CreatedAt >= start)
            .Select(p => p.PostId).ToListAsync(ct);
        var posts = await db.CreatorPosts.AsNoTracking().Where(p => p.CreatorId == user.Id && !p.Deleted).Select(p => new { p.Id, p.Title }).ToListAsync(ct);
        var top = posts.Select(p => new CreatorPostStatsDto(p.Id, p.Title,
                activity.Count(a => a.PostId == p.Id && a.Kind == CreatorActivity.View),
                activity.Count(a => a.PostId == p.Id && a.Kind == CreatorActivity.Play),
                unlocks.Count(u => u == p.Id)))
            .OrderByDescending(p => p.Views).ThenByDescending(p => p.Unlocks).Take(10).ToList();
        return new CreatorAnalyticsDto(visitors, newSubs, visitors == 0 ? 0 : Math.Round(100d * newSubs / visitors, 1),
            activity.Count(a => a.Kind == CreatorActivity.View), activity.Count(a => a.Kind == CreatorActivity.Play), unlocks.Count, top);
    }

    // ---- Shapes and helpers ----

    internal async Task<IReadOnlyList<CreatorSubscriptionDto>> SubscriptionDtosAsync(IReadOnlyList<CreatorSubscription> subs, CancellationToken ct)
    {
        if (subs.Count == 0)
            return [];
        var creatorIds = subs.Select(s => s.CreatorId).Distinct().ToList();
        var tierIds = subs.Select(s => s.TierId).Distinct().ToList();
        var subIds = subs.Select(s => s.Id).ToList();
        var creators = await db.CreatorProfiles.AsNoTracking().Where(c => creatorIds.Contains(c.UserId)).ToDictionaryAsync(c => c.UserId, c => c.Name, ct);
        var tiers = await db.CreatorTiers.AsNoTracking().Where(t => tierIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var payments = await db.CreatorPayments.AsNoTracking().Where(p => p.SubscriptionId != null && subIds.Contains(p.SubscriptionId.Value))
            .OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
        var transferIds = payments.Select(p => p.BankTransferId).ToList();
        var errors = await db.BankTransfers.AsNoTracking().Where(t => transferIds.Contains(t.Id) && t.Error != null)
            .ToDictionaryAsync(t => t.Id, t => t.Error, ct);
        return subs.Select(s =>
        {
            var last = payments.FirstOrDefault(p => p.SubscriptionId == s.Id);
            return new CreatorSubscriptionDto(s.Id, s.CreatorId, creators.GetValueOrDefault(s.CreatorId, ""), s.TierId, tiers.GetValueOrDefault(s.TierId, ""),
                s.Period, s.Price, s.Status, s.StartedAt, s.CurrentPeriodEnd, s.CancelAtPeriodEnd, s.GraceUntil,
                last?.Status, last is null ? null : errors.GetValueOrDefault(last.BankTransferId));
        }).ToList();
    }

    internal static string Required(string? value, int max, string message) =>
        Optional(value, max) ?? throw new ChatRejectedException(message);

    internal static string? Optional(string? value, int max)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed))
            return null;
        return trimmed.Length > max ? throw new ChatRejectedException($"Keep it under {max:N0} characters.") : trimmed;
    }

    static string SafeUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u) && u.Scheme is "https" or "http"
            ? u.ToString()
            : throw new ChatRejectedException("Links must be web addresses starting with https://.");

    static Guid Deterministic(Guid a, Guid b)
    {
        Span<byte> input = stackalloc byte[32];
        a.TryWriteBytes(input[..16]);
        b.TryWriteBytes(input[16..]);
        return new Guid(System.Security.Cryptography.SHA256.HashData(input)[..16]);
    }
}
