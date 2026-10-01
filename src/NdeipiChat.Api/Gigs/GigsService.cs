using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Assets;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Gigs;

public sealed class GigsOptions
{
    public const string Section = "Gigs";

    /// <summary>What gigs are priced and paid in: a token listed under Tokens:Known.</summary>
    public string TokenSymbol { get; set; } = "NDEIPI";
}

/// <summary>
/// Gigs: worker profiles on a map (SRS FR-01), gigs from the map or a chat (FR-02), dispatch to the
/// nearest suitable available workers (FR-03), and payment in NdeipiCoin from client to worker with
/// no platform fee (FR-04). Gigs move between statuses only through conditional UPDATEs, so two
/// workers accepting at once can't both get one.
/// </summary>
public sealed class GigsService(
    ChatDbContext db,
    ConversationService conversations,
    MessageService messages,
    ChatNotifier notifier,
    IOptions<GigsOptions> gigOptions,
    IOptions<TokenOptions> tokenOptions,
    IEnumerable<IWorkRecordListener> workListeners,
    TimeProvider clock)
{
    public const int MaxMapWorkers = 500;

    static readonly string[] Active = [GigStatuses.Open, GigStatuses.Assigned, GigStatuses.Submitted];

    // ---- Profiles (FR-01) ----

    public async Task<GigProfileDto?> ProfileAsync(User me, Guid userId, CancellationToken ct)
    {
        var profile = await db.GigProfiles.AsNoTracking().Include(p => p.User).FirstOrDefaultAsync(p => p.UserId == userId, ct);
        return profile is null ? null : ToDto(profile, exact: userId == me.Id);
    }

    public async Task<GigProfileDto> SaveProfileAsync(User me, SaveGigProfileRequest request, CancellationToken ct)
    {
        var headline = request.Headline?.Trim() ?? "";
        if (headline.Length is 0 or > 160)
            throw new ChatRejectedException("Describe what you do in up to 160 characters.");
        var skills = (request.Skills ?? []).Select(s => s?.Trim().ToLowerInvariant() ?? "").Distinct().ToList();
        if (skills.Count is 0 or > GigsContract.MaxSkills || skills.Any(s => !GigSkills.All.Contains(s)))
            throw new ChatRejectedException($"Pick 1 to {GigsContract.MaxSkills} skills.");
        CheckPlace(request.Latitude, request.Longitude);
        var region = request.Region?.Trim() is { Length: > 0 } r ? r.Length <= 80 ? r : throw new ChatRejectedException("Keep the area under 80 characters.") : null;

        var profile = await db.GigProfiles.Include(p => p.User).FirstOrDefaultAsync(p => p.UserId == me.Id, ct);
        if (profile is null)
        {
            profile = new GigProfile { UserId = me.Id, User = me };
            db.GigProfiles.Add(profile);
        }
        (profile.Headline, profile.Skills, profile.Region, profile.Latitude, profile.Longitude, profile.UpdatedAt) =
            (headline, string.Join(',', skills), region, request.Latitude, request.Longitude, clock.GetUtcNow());
        await db.SaveChangesAsync(ct);

        if (profile.IsAvailable)
        {
            await notifier.PublishAsync(GigsContract.MapTopic, ToMapWorker(profile));
            await OfferOpenGigsToAsync(profile, ct);
        }
        return ToDto(profile, exact: true);
    }

    /// <summary>The active/offline toggle: shows or hides the worker on the map, and makes them dispatchable.</summary>
    public async Task<GigProfileDto> SetAvailabilityAsync(User me, AvailabilityRequest request, CancellationToken ct)
    {
        var profile = await db.GigProfiles.Include(p => p.User).FirstOrDefaultAsync(p => p.UserId == me.Id, ct)
            ?? throw new ChatRejectedException("Set up your Gigs profile first.");
        if (request is { Latitude: { } lat, Longitude: { } lng })
        {
            CheckPlace(lat, lng);
            (profile.Latitude, profile.Longitude) = (lat, lng);
        }
        profile.IsAvailable = request.IsAvailable;
        profile.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);

        await notifier.PublishAsync(GigsContract.MapTopic, ToMapWorker(profile));
        if (profile.IsAvailable)
            await OfferOpenGigsToAsync(profile, ct);
        return ToDto(profile, exact: true);
    }

    static void CheckPlace(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || latitude is < -90 or > 90 || longitude is < -180 or > 180)
            throw new ChatRejectedException("Pick a place on the map.");
    }

    // ---- Map ----

    public async Task<GigMapDto> MapAsync(User me, CancellationToken ct)
    {
        var workers = await db.GigProfiles.AsNoTracking().Include(p => p.User)
            .Where(p => p.IsAvailable && p.UserId != me.Id)
            .OrderByDescending(p => p.UpdatedAt)
            .Take(MaxMapWorkers)
            .ToListAsync(ct);
        return new GigMapDto(workers.Select(ToMapWorker).ToList(), await MineAsync(me, activeOnly: true, ct));
    }

    // ---- Gigs (FR-02) ----

    public async Task<IReadOnlyList<GigDto>> MineAsync(User me, bool activeOnly, CancellationToken ct)
    {
        var offered = db.GigOffers.Where(o => o.WorkerId == me.Id && o.Status == GigOfferStatuses.Offered).Select(o => o.GigId);
        var gigs = await db.Gigs.AsNoTracking().Include(g => g.Client).Include(g => g.Worker)
            .Where(g => g.ClientId == me.Id || g.WorkerId == me.Id || (g.Status == GigStatuses.Open && offered.Contains(g.Id)))
            .Where(g => !activeOnly || Active.Contains(g.Status))
            .OrderByDescending(g => g.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        return await ToDtosAsync(gigs, me, ct);
    }

    public async Task<GigDto?> GetAsync(User me, Guid id, CancellationToken ct)
    {
        var gig = await LoadAsync(id, ct);
        if (gig is null || !await CanSeeAsync(gig, me, ct))
            return null;
        return (await ToDtosAsync([gig], me, ct))[0];
    }

    /// <summary>Open gigs are on the board for everyone; after that, only the people involved see them.</summary>
    async Task<bool> CanSeeAsync(Gig gig, User me, CancellationToken ct) =>
        gig.Status == GigStatuses.Open || gig.ClientId == me.Id || gig.WorkerId == me.Id
        || await db.GigOffers.AnyAsync(o => o.GigId == gig.Id && o.WorkerId == me.Id, ct);

    // ---- The board ----

    public async Task<IReadOnlyList<GigCategoryDto>> CategoriesAsync(CancellationToken ct)
    {
        var open = await db.Gigs.AsNoTracking().Where(g => g.Status == GigStatuses.Open)
            .GroupBy(g => g.Skill)
            .Select(x => new { Skill = x.Key, Count = x.Count(), Latest = x.Max(g => g.CreatedAt) })
            .ToListAsync(ct);
        return GigSkills.All.Select(s => open.FirstOrDefault(o => o.Skill == s) is { } o
                ? new GigCategoryDto(s, o.Count, o.Latest)
                : new GigCategoryDto(s, 0, null))
            .ToList();
    }

    public const int BoardPageSize = 60;

    /// <summary>Open gigs, newest first, in one category or all; with how far each is from your work profile.</summary>
    public async Task<IReadOnlyList<GigDto>> BoardAsync(User me, string? skill, CancellationToken ct)
    {
        var query = db.Gigs.AsNoTracking().Include(g => g.Client).Include(g => g.Worker).Where(g => g.Status == GigStatuses.Open);
        if (skill?.Trim().ToLowerInvariant() is { Length: > 0 } s)
            query = query.Where(g => g.Skill == s);
        var gigs = await query.OrderByDescending(g => g.CreatedAt).Take(BoardPageSize).ToListAsync(ct);
        var profile = await db.GigProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == me.Id, ct);
        return await ToDtosAsync(gigs, me, ct, profile);
    }

    public async Task<GigDto> CreateAsync(User me, CreateGigRequest request, CancellationToken ct)
    {
        var title = request.Title?.Trim() ?? "";
        if (title.Length is 0 or > 120)
            throw new ChatRejectedException("Give the gig a title of up to 120 characters.");
        var description = request.Description?.Trim() ?? "";
        if (description.Length > 2000)
            throw new ChatRejectedException("Keep the description under 2000 characters.");
        var skill = request.Skill?.Trim().ToLowerInvariant() ?? "";
        if (!GigSkills.All.Contains(skill))
            throw new ChatRejectedException("Pick the kind of work.");
        CheckPlace(request.Latitude, request.Longitude);
        var region = request.Region?.Trim() is { Length: > 0 } r ? r[..Math.Min(r.Length, 80)] : null;
        var token = Token();
        if (!Amounts.TryParse(request.Budget, token.Decimals ?? AssetTransferHandler.MaxDecimals, out var budget) || budget > 1_000_000_000m)
            throw new ChatRejectedException($"Set a budget in {token.Symbol} greater than zero.");

        // Started from a chat: in a direct chat, the gig is for the other person.
        var workerId = request.WorkerId;
        if (request.ConversationId is { } conversationId)
        {
            var members = await db.Members.Where(m => m.ConversationId == conversationId).Select(m => m.UserId).ToListAsync(ct);
            if (!members.Contains(me.Id))
                throw new ChatRejectedException("You aren't in that chat.");
            var isDirect = await db.Conversations.AnyAsync(c => c.Id == conversationId && c.Type == ConversationType.Direct, ct);
            if (workerId is null && isDirect)
                workerId = members.FirstOrDefault(m => m != me.Id);
        }

        GigProfile? chosen = null;
        if (workerId is { } wid)
        {
            if (wid == me.Id)
                throw new ChatRejectedException("You can't hire yourself.");
            chosen = await db.GigProfiles.AsNoTracking().Include(p => p.User).FirstOrDefaultAsync(p => p.UserId == wid, ct)
                ?? throw new ChatRejectedException("They haven't set up a Gigs profile yet, so they can't take gigs.");
        }

        var now = clock.GetUtcNow();
        var gig = new Gig
        {
            Id = Guid.NewGuid(),
            ClientId = me.Id,
            Client = me,
            Title = title,
            Description = description,
            Skill = skill,
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Region = region,
            Budget = budget,
            TokenSymbol = token.Symbol,
            Status = GigStatuses.Open,
            SourceConversationId = request.ConversationId,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Gigs.Add(gig);
        await db.SaveChangesAsync(ct);

        if (chosen is not null)
            await OfferAsync(gig, [(chosen.UserId, Distance(gig, chosen))], ct);
        else
            await DispatchAsync(gig, ct);
        return (await ToDtosAsync([gig], me, ct))[0];
    }

    // ---- Dispatch (FR-03) ----

    /// <summary>Offers the gig to the nearest available workers with the skill who haven't had it yet.</summary>
    public async Task<int> DispatchAsync(Gig gig, CancellationToken ct)
    {
        var band = GigsContract.DispatchRadiusKm / 111.0;
        var already = db.GigOffers.Where(o => o.GigId == gig.Id).Select(o => o.WorkerId);
        var candidates = await db.GigProfiles.AsNoTracking()
            .Where(p => p.IsAvailable && p.UserId != gig.ClientId && !already.Contains(p.UserId)
                && p.Latitude >= gig.Latitude - band && p.Latitude <= gig.Latitude + band
                && p.Skills.Contains(gig.Skill))
            .Take(2000)
            .ToListAsync(ct);
        var nearest = candidates
            .Where(p => p.Skills.Split(',').Contains(gig.Skill))
            .Select(p => (p.UserId, Km: Distance(gig, p)))
            .Where(x => x.Km <= GigsContract.DispatchRadiusKm)
            .OrderBy(x => x.Km)
            .Take(GigsContract.DispatchFanOut)
            .ToList();
        await OfferAsync(gig, nearest, ct);
        return nearest.Count;
    }

    /// <summary>A worker just came online: offer them open gigs nearby that nobody has taken.</summary>
    async Task OfferOpenGigsToAsync(GigProfile worker, CancellationToken ct)
    {
        var band = GigsContract.DispatchRadiusKm / 111.0;
        var skills = worker.Skills.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var offered = db.GigOffers.Where(o => o.WorkerId == worker.UserId).Select(o => o.GigId);
        var gigs = await db.Gigs.Include(g => g.Client)
            .Where(g => g.Status == GigStatuses.Open && g.ClientId != worker.UserId && skills.Contains(g.Skill) && !offered.Contains(g.Id)
                && g.Latitude >= worker.Latitude - band && g.Latitude <= worker.Latitude + band)
            .OrderByDescending(g => g.CreatedAt)
            .Take(200)
            .ToListAsync(ct);
        foreach (var gig in gigs.Where(g => Distance(g, worker) <= GigsContract.DispatchRadiusKm).Take(10))
            await OfferAsync(gig, [(worker.UserId, Distance(gig, worker))], ct);
    }

    async Task OfferAsync(Gig gig, IReadOnlyList<(Guid WorkerId, double Km)> workers, CancellationToken ct)
    {
        if (workers.Count == 0)
            return;
        var now = clock.GetUtcNow();
        foreach (var (workerId, km) in workers)
            db.GigOffers.Add(new GigOffer { GigId = gig.Id, WorkerId = workerId, DistanceKm = Math.Round(km, 1), Status = GigOfferStatuses.Offered, CreatedAt = now });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Offered twice at once (a worker toggled on as the gig was posted): the first stands.
            foreach (var entry in db.ChangeTracker.Entries<GigOffer>().Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
        }
        await NotifyAsync(gig.Id, "offer", workers.Select(w => w.WorkerId), ct);
    }

    // ---- Lifecycle ----

    /// <summary>
    /// First worker to accept gets the gig; the client and worker get a direct chat for it. Workers
    /// it was offered to can accept, and so can anyone with the skill who finds it on the board.
    /// </summary>
    public async Task<GigDto?> AcceptAsync(User me, Guid id, CancellationToken ct)
    {
        var gig = await LoadAsync(id, ct);
        if (gig is null)
            return null;
        if (!await db.GigOffers.AnyAsync(o => o.GigId == id && o.WorkerId == me.Id && o.Status == GigOfferStatuses.Offered, ct))
        {
            // From the board: they need a work profile with the skill, and mustn't have turned it down.
            if (gig.Status != GigStatuses.Open || gig.ClientId == me.Id)
                return null;
            var profile = await db.GigProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == me.Id, ct)
                ?? throw new ChatRejectedException("Set up your work profile (the Work tab) to take gigs.");
            if (!profile.Skills.Split(',').Contains(gig.Skill))
                throw new ChatRejectedException($"Add {GigSkills.Label(gig.Skill)} to your skills to take this gig.");
            if (await db.GigOffers.AnyAsync(o => o.GigId == id && o.WorkerId == me.Id, ct))
                throw new ChatRejectedException("You've already passed on this gig.");
            await OfferAsync(gig, [(me.Id, Distance(gig, profile))], ct);
        }

        var now = clock.GetUtcNow();
        var taken = await db.Gigs.Where(g => g.Id == id && g.Status == GigStatuses.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.Status, GigStatuses.Assigned)
                .SetProperty(g => g.WorkerId, me.Id)
                .SetProperty(g => g.AssignedAt, now)
                .SetProperty(g => g.UpdatedAt, now), ct);
        if (taken == 0)
            throw new ChatRejectedException(gig.Status == GigStatuses.Cancelled ? "The client cancelled this gig." : "Someone else took this gig.");

        var others = await CloseOffersAsync(id, except: me.Id, ct);
        await db.GigOffers.Where(o => o.GigId == id && o.WorkerId == me.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, GigOfferStatuses.Accepted), ct);

        // The gig's thread: their direct chat, opened with a note from the worker.
        var chat = await conversations.CreateAsync(me, new CreateConversationRequest(ConversationType.Direct, [gig.ClientId], null), ct);
        await db.Gigs.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.ConversationId, chat.Id), ct);
        await messages.SendAsync(me, new SendMessageRequest(chat.Id, MessageKinds.Text,
            ContractJson.ToElement(new TextPayload($"I've taken your gig \"{gig.Title}\". Let's agree the details here.")), Deterministic(id, 1)), ct);

        await NotifyAsync(id, "gig", [gig.ClientId, .. others], ct);
        return await GetAsync(me, id, ct);
    }

    public async Task<GigDto?> DeclineAsync(User me, Guid id, CancellationToken ct)
    {
        var declined = await db.GigOffers.Where(o => o.GigId == id && o.WorkerId == me.Id && o.Status == GigOfferStatuses.Offered)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, GigOfferStatuses.Declined), ct);
        if (declined == 0)
            return null;
        // Nobody left considering it: offer it further out.
        var gig = await LoadAsync(id, ct);
        if (gig is { Status: GigStatuses.Open } && !await db.GigOffers.AnyAsync(o => o.GigId == id && o.Status == GigOfferStatuses.Offered, ct))
            await DispatchAsync(gig, ct);
        return await GetAsync(me, id, ct);
    }

    /// <summary>The worker says it's done.</summary>
    public Task<GigDto?> SubmitAsync(User me, Guid id, CancellationToken ct) =>
        MoveAsync(me, id, g => g.WorkerId == me.Id, [GigStatuses.Assigned], GigStatuses.Submitted, "This gig isn't in progress.", ct);

    public async Task<GigDto?> CancelAsync(User me, Guid id, CancellationToken ct)
    {
        var gig = await MoveAsync(me, id, g => g.ClientId == me.Id, [GigStatuses.Open, GigStatuses.Assigned], GigStatuses.Cancelled,
            "Only open or in-progress gigs can be cancelled.", ct);
        if (gig is not null)
            await NotifyAsync(id, "gig", await CloseOffersAsync(id, except: null, ct), ct);
        return gig;
    }

    /// <summary>
    /// The client approves the work and pays: the budget goes from their wallet to the worker's in
    /// NdeipiCoin, as a transfer in the gig's chat, with no fee taken. Calling it again after a
    /// failed send retries the payment; the chat's client message id stops it paying twice.
    /// </summary>
    public async Task<GigDto?> ApproveAsync(User me, Guid id, CancellationToken ct)
    {
        var gig = await LoadAsync(id, ct);
        if (gig is null || gig.ClientId != me.Id)
            return null;
        if (gig.Status != GigStatuses.Completed)
        {
            var now = clock.GetUtcNow();
            var done = await db.Gigs.Where(g => g.Id == id && (g.Status == GigStatuses.Assigned || g.Status == GigStatuses.Submitted))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(g => g.Status, GigStatuses.Completed)
                    .SetProperty(g => g.CompletedAt, now)
                    .SetProperty(g => g.UpdatedAt, now), ct);
            if (done == 0)
                throw new ChatRejectedException("This gig can't be approved now.");
            await db.GigProfiles.Where(p => p.UserId == gig.WorkerId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.CompletedGigs, p => p.CompletedGigs + 1), ct);
            if (gig.WorkerId is { } doneBy)
                foreach (var listener in workListeners)
                    await listener.WorkRecordChangedAsync(doneBy, ct);
        }

        if (gig.PaymentMessageId is null && gig.ConversationId is { } chat && gig.WorkerId is { } worker)
        {
            var token = Token();
            var paid = await messages.SendAsync(me, new SendMessageRequest(chat, MessageKinds.AssetTransfer,
                ContractJson.ToElement(new AssetTransferPayload(worker, token, Amounts.Format(gig.Budget), $"Gig: {gig.Title}")),
                Deterministic(id, 2)), ct);
            await db.Gigs.Where(g => g.Id == id).ExecuteUpdateAsync(s => s.SetProperty(g => g.PaymentMessageId, paid.Id), ct);
        }

        await NotifyAsync(id, "gig", gig.WorkerId is { } w ? [w] : [], ct);
        return await GetAsync(me, id, ct);
    }

    /// <summary>Each side rates the other once the gig is done; the worker's stars go on their profile.</summary>
    public async Task<GigDto?> RateAsync(User me, Guid id, int stars, CancellationToken ct)
    {
        if (stars is < 1 or > 5)
            throw new ChatRejectedException("Rate from 1 to 5 stars.");
        var gig = await LoadAsync(id, ct);
        if (gig is null || (gig.ClientId != me.Id && gig.WorkerId != me.Id))
            return null;
        if (gig.Status != GigStatuses.Completed)
            throw new ChatRejectedException("You can rate a gig once it's done.");

        if (gig.ClientId == me.Id)
        {
            var rated = await db.Gigs.Where(g => g.Id == id && g.WorkerStars == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.WorkerStars, stars), ct);
            if (rated == 0)
                throw new ChatRejectedException("You've already rated this gig.");
            await db.GigProfiles.Where(p => p.UserId == gig.WorkerId)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.RatingSum, p => p.RatingSum + stars).SetProperty(p => p.RatingCount, p => p.RatingCount + 1), ct);
            if (gig.WorkerId is { } ratedWorker)
                foreach (var listener in workListeners)
                    await listener.WorkRecordChangedAsync(ratedWorker, ct);
        }
        else
        {
            var rated = await db.Gigs.Where(g => g.Id == id && g.ClientStars == null)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.ClientStars, stars), ct);
            if (rated == 0)
                throw new ChatRejectedException("You've already rated this gig.");
        }
        return await GetAsync(me, id, ct);
    }

    async Task<GigDto?> MoveAsync(User me, Guid id, Func<Gig, bool> allowed, string[] from, string to, string refusal, CancellationToken ct)
    {
        var gig = await LoadAsync(id, ct);
        if (gig is null || !allowed(gig))
            return null;
        var moved = await db.Gigs.Where(g => g.Id == id && from.Contains(g.Status))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.Status, to).SetProperty(g => g.UpdatedAt, clock.GetUtcNow()), ct);
        if (moved == 0)
            throw new ChatRejectedException(refusal);
        var other = gig.ClientId == me.Id ? gig.WorkerId : gig.ClientId;
        await NotifyAsync(id, "gig", other is { } o ? [o] : [], ct);
        return await GetAsync(me, id, ct);
    }

    /// <returns>The workers whose offers were closed.</returns>
    async Task<List<Guid>> CloseOffersAsync(Guid gigId, Guid? except, CancellationToken ct)
    {
        var open = await db.GigOffers.Where(o => o.GigId == gigId && o.Status == GigOfferStatuses.Offered && o.WorkerId != except)
            .Select(o => o.WorkerId).ToListAsync(ct);
        await db.GigOffers.Where(o => o.GigId == gigId && o.Status == GigOfferStatuses.Offered && o.WorkerId != except)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, GigOfferStatuses.Closed), ct);
        return open;
    }

    /// <summary>Each person gets the gig as they're allowed to see it, on their own topic.</summary>
    async Task NotifyAsync(Guid gigId, string kind, IEnumerable<Guid> userIds, CancellationToken ct)
    {
        var gig = await LoadAsync(gigId, ct);
        if (gig is null)
            return;
        foreach (var userId in userIds.Distinct())
        {
            var user = await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct);
            var dto = (await ToDtosAsync([gig], user, ct))[0];
            await notifier.PublishAsync(GigsContract.UserTopic(userId), new GigNewsDto(kind, dto));
        }
    }

    Task<Gig?> LoadAsync(Guid id, CancellationToken ct) =>
        db.Gigs.AsNoTracking().Include(g => g.Client).Include(g => g.Worker).FirstOrDefaultAsync(g => g.Id == id, ct);

    TokenRef Token()
    {
        var symbol = gigOptions.Value.TokenSymbol;
        var known = tokenOptions.Value.Known.FirstOrDefault(k => k.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase))
            ?? throw new ChatRejectedException($"Paying in {symbol} isn't set up on this server.");
        return known.ToTokenRef();
    }

    /// <summary>A fixed client message id per gig and purpose, so a retried send can't post (or pay) twice.</summary>
    static Guid Deterministic(Guid gigId, byte purpose)
    {
        var bytes = gigId.ToByteArray();
        bytes[15] ^= purpose;
        bytes[14] ^= 0x5A;
        return new Guid(bytes);
    }

    static double Distance(Gig gig, GigProfile p) => GigsContract.DistanceKm(gig.Latitude, gig.Longitude, p.Latitude, p.Longitude);

    // ---- Mapping ----

    async Task<List<GigDto>> ToDtosAsync(IReadOnlyList<Gig> gigs, User viewer, CancellationToken ct, GigProfile? viewerProfile = null)
    {
        var ids = gigs.Select(g => g.Id).ToList();
        var offers = await db.GigOffers.AsNoTracking().Where(o => o.WorkerId == viewer.Id && ids.Contains(o.GigId)).ToDictionaryAsync(o => o.GigId, ct);
        var paymentIds = gigs.Where(g => g.PaymentMessageId is not null).Select(g => (Guid?)g.PaymentMessageId).ToList();
        var payments = paymentIds.Count == 0
            ? []
            : await db.TokenTransfers.AsNoTracking().Where(t => paymentIds.Contains(t.MessageId))
                .Select(t => new { t.MessageId, t.Status }).ToDictionaryAsync(t => t.MessageId!.Value, t => t.Status, ct);

        return gigs.Select(g =>
        {
            var party = g.ClientId == viewer.Id || g.WorkerId == viewer.Id;
            var offer = offers.GetValueOrDefault(g.Id);
            return new GigDto(
                g.Id, g.Title, g.Description, g.Skill,
                party ? g.Latitude : GigsContract.Approximate(g.Latitude),
                party ? g.Longitude : GigsContract.Approximate(g.Longitude),
                g.Region, Amounts.Format(g.Budget), g.TokenSymbol, g.Status,
                ChatMapper.ToDto(g.Client), g.Worker is null ? null : ChatMapper.ToDto(g.Worker),
                party ? g.ConversationId : null,
                g.CreatedAt, g.CompletedAt,
                g.PaymentMessageId is { } m ? payments.GetValueOrDefault(m, TransferStatuses.Pending) : null,
                offer?.DistanceKm ?? (viewerProfile is null ? null : (double?)Math.Round(Distance(g, viewerProfile), 1)),
                g.ClientId == viewer.Id, g.WorkerId == viewer.Id,
                offer?.Status,
                g.WorkerStars, g.ClientStars);
        }).ToList();
    }

    static GigProfileDto ToDto(GigProfile p, bool exact) => new(
        p.UserId, p.User.DisplayName, p.User.AvatarUrl, p.Headline,
        p.Skills.Split(',', StringSplitOptions.RemoveEmptyEntries), p.Region,
        exact ? p.Latitude : GigsContract.Approximate(p.Latitude),
        exact ? p.Longitude : GigsContract.Approximate(p.Longitude),
        p.IsAvailable, p.CompletedGigs,
        p.RatingCount == 0 ? null : Math.Round((double)p.RatingSum / p.RatingCount, 1), p.RatingCount);

    static MapWorkerDto ToMapWorker(GigProfile p) => new(
        p.UserId, p.User.DisplayName, p.Skills.Split(',', StringSplitOptions.RemoveEmptyEntries),
        GigsContract.Approximate(p.Latitude), GigsContract.Approximate(p.Longitude),
        p.RatingCount == 0 ? null : Math.Round((double)p.RatingSum / p.RatingCount, 1), p.IsAvailable);
}

/// <summary>"gigs:{userId}" is only for that user; "gigs-map:all" for anyone who may use Gigs.</summary>
public sealed class GigUserTopicPolicy : ITopicPolicy
{
    public string Prefix => "gigs";

    public Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Task.FromResult(key == user.Id.ToString("N"));
}

public sealed class GigMapTopicPolicy(LauncherService launcher) : ITopicPolicy
{
    public string Prefix => "gigs-map";

    public Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Task.FromResult(key == "all" && launcher.CanUse(user, GigsContract.AppId));
}

/// <summary>Told when someone's work record changes (a gig of theirs was approved, or rated): the Trust Score counts it.</summary>
public interface IWorkRecordListener
{
    Task WorkRecordChangedAsync(Guid workerId, CancellationToken ct);
}
