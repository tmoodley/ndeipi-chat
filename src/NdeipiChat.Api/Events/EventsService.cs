using System.Globalization;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Events;

/// <summary>
/// The catalog, organizers' events and the buying path: hold seats, pay, get tickets. Seats move
/// only through conditional UPDATEs on <see cref="TicketTier"/> (Capacity - Sold - Held &gt;= n), so
/// two buyers racing for the last seat can't both get it -- SQL Server's row lock decides.
/// </summary>
public sealed class EventsService(
    ChatDbContext db,
    TicketIssuer issuer,
    BankingService banking,
    IOptions<BridgeOptions> bridge,
    TimeProvider clock)
{
    public const int MaxTiers = 10;
    public const int MaxPageSize = 50;

    /// <summary>Events stay in the catalog until a few hours after they start.</summary>
    public static readonly TimeSpan ListedAfterStart = TimeSpan.FromHours(6);

    public static bool IsOrganizer(User user) =>
        user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(EventsContract.OrganizerRole, StringComparer.OrdinalIgnoreCase);

    public async Task<EventPageDto> ListAsync(string? city, string? category, int skip, int take, CancellationToken ct)
    {
        var since = clock.GetUtcNow() - ListedAfterStart;
        var upcoming = db.Events.AsNoTracking().Where(e => e.IsPublished && e.StartsAt > since);
        var cities = await upcoming.Select(e => e.City).Distinct().OrderBy(c => c).Take(100).ToListAsync(ct);

        var query = upcoming;
        if (!string.IsNullOrWhiteSpace(city))
            query = query.Where(e => e.City == city.Trim());
        if (!string.IsNullOrWhiteSpace(category))
            query = query.Where(e => e.Category == category.Trim().ToLowerInvariant());

        var total = await query.CountAsync(ct);
        var events = await query.OrderBy(e => e.StartsAt).ThenBy(e => e.Id)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, MaxPageSize))
            .Include(e => e.Tiers)
            .ToListAsync(ct);
        return new EventPageDto(events.Select(ToSummary).ToList(), total, cities);
    }

    public async Task<IReadOnlyList<EventSummaryDto>> MineAsync(User me, CancellationToken ct)
    {
        var validating = db.EventValidators.Where(v => v.UserId == me.Id).Select(v => v.EventId);
        var events = await db.Events.AsNoTracking()
            .Where(e => e.OrganizerId == me.Id || validating.Contains(e.Id))
            .OrderByDescending(e => e.StartsAt)
            .Include(e => e.Tiers)
            .Take(200)
            .ToListAsync(ct);
        return events.Select(ToSummary).ToList();
    }

    public async Task<EventDetailDto?> GetAsync(User me, Guid id, CancellationToken ct)
    {
        var e = await db.Events.AsNoTracking().Include(e => e.Organizer).Include(e => e.Tiers).FirstOrDefaultAsync(e => e.Id == id, ct);
        if (e is null)
            return null;
        var canManage = e.OrganizerId == me.Id;
        var canScan = canManage || await db.EventValidators.AnyAsync(v => v.EventId == id && v.UserId == me.Id, ct);
        if (!e.IsPublished && !canScan)
            return null;
        return ToDetail(e, canManage, canScan);
    }

    public async Task<EventDetailDto> CreateAsync(User me, SaveEventRequest request, CancellationToken ct)
    {
        if (!IsOrganizer(me))
            throw new ChatRejectedException("Only organizers can create events.");
        var now = clock.GetUtcNow();
        var e = new EventListing
        {
            Id = Guid.NewGuid(),
            OrganizerId = me.Id,
            Organizer = me,
            Title = "",
            Category = "",
            City = "",
            Venue = "",
            Currency = bridge.Value.Currency.ToLowerInvariant(),
            CreatedAt = now
        };
        Apply(e, request, now);
        db.Events.Add(e);
        await db.SaveChangesAsync(ct);
        return ToDetail(e, true, true);
    }

    public async Task<EventDetailDto?> UpdateAsync(User me, Guid id, SaveEventRequest request, CancellationToken ct)
    {
        var e = await db.Events.Include(e => e.Organizer).Include(e => e.Tiers).FirstOrDefaultAsync(e => e.Id == id && e.OrganizerId == me.Id, ct);
        if (e is null)
            return null;
        Apply(e, request, clock.GetUtcNow());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ChatRejectedException("Tickets sold while you were editing; reload and try again.");
        }
        catch (DbUpdateException)
        {
            // The capacity check constraint: seats were held or sold between reading and saving.
            throw new ChatRejectedException("A tier can't hold fewer seats than are already sold or held.");
        }
        return ToDetail(e, true, true);
    }

    public async Task<EventDetailDto?> SetPublishedAsync(User me, Guid id, bool published, CancellationToken ct)
    {
        var e = await db.Events.Include(e => e.Organizer).Include(e => e.Tiers).FirstOrDefaultAsync(e => e.Id == id && e.OrganizerId == me.Id, ct);
        if (e is null)
            return null;
        if (published && e.Tiers.Count == 0)
            throw new ChatRejectedException("Add at least one ticket tier before publishing.");
        e.IsPublished = published;
        e.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return ToDetail(e, true, true);
    }

    void Apply(EventListing e, SaveEventRequest request, DateTimeOffset now)
    {
        e.Title = Required(request.Title, 160, "a title");
        e.Description = request.Description?.Trim() is { Length: > 0 } d
            ? d.Length <= 4000 ? d : throw new ChatRejectedException("The description is limited to 4000 characters.")
            : "";
        var category = request.Category?.Trim().ToLowerInvariant() ?? "";
        e.Category = EventCategories.All.Contains(category) ? category : throw new ChatRejectedException("Pick a category.");
        e.City = Required(request.City, 80, "a city");
        e.Venue = Required(request.Venue, 160, "a venue");
        if (request.StartsAt < now && request.StartsAt != e.StartsAt)
            throw new ChatRejectedException("The event can't start in the past.");
        e.StartsAt = request.StartsAt;
        e.UpdatedAt = now;

        var tiers = request.Tiers ?? [];
        if (tiers.Count > MaxTiers)
            throw new ChatRejectedException($"An event can have up to {MaxTiers} ticket tiers.");

        foreach (var gone in e.Tiers.Where(t => !tiers.Any(r => r.Id == t.Id)).ToList())
        {
            if (gone.Sold + gone.Held > 0)
                throw new ChatRejectedException($"\"{gone.Name}\" has tickets sold or held, so it can't be removed.");
            e.Tiers.Remove(gone);
        }

        for (var i = 0; i < tiers.Count; i++)
        {
            var r = tiers[i];
            var tier = r.Id is { } tierId ? e.Tiers.FirstOrDefault(t => t.Id == tierId) : null;
            if (tier is null)
            {
                tier = new TicketTier { Id = Guid.NewGuid(), EventId = e.Id, Name = "" };
                e.Tiers.Add(tier);
            }
            tier.Name = Required(r.Name, 80, "each tier a name");
            tier.Price = ParsePrice(r.Price);
            if (r.Capacity is < 1 or > 1_000_000)
                throw new ChatRejectedException("A tier holds between 1 and 1,000,000 tickets.");
            if (r.Capacity < tier.Sold + tier.Held)
                throw new ChatRejectedException($"\"{tier.Name}\" already has {tier.Sold + tier.Held} tickets sold or held.");
            tier.Capacity = r.Capacity;
            tier.Position = i;
        }
    }

    static decimal ParsePrice(string? text)
    {
        if (decimal.TryParse(text?.Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var zero) && zero == 0)
            return 0;
        return Amounts.TryParse(text, BankTransferHandler.MaxDecimals, out var price) && price <= 1_000_000
            ? price
            : throw new ChatRejectedException("Give each tier a price, to the cent (0 for free).");
    }

    static string Required(string? value, int max, string what)
    {
        var text = value?.Trim() ?? "";
        return text.Length is > 0 and var n && n <= max ? text : throw new ChatRejectedException($"Give {what} of up to {max} characters.");
    }

    // ---- Validators ----

    public async Task<IReadOnlyList<UserDto>?> ValidatorsAsync(User me, Guid id, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id && e.OrganizerId == me.Id, ct))
            return null;
        return await db.EventValidators.Where(v => v.EventId == id)
            .Join(db.Users, v => v.UserId, u => u.Id, (v, u) => new UserDto(u.Id, u.DisplayName, u.Username, u.AvatarUrl))
            .ToListAsync(ct);
    }

    public async Task<UserDto?> AddValidatorAsync(User me, Guid id, string? email, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id && e.OrganizerId == me.Id, ct))
            return null;
        var normalized = email?.Trim().ToLowerInvariant();
        var user = string.IsNullOrEmpty(normalized) ? null : await db.Users.FirstOrDefaultAsync(u => u.EmailVerified && u.Email == normalized, ct);
        if (user is null)
            throw new ChatRejectedException("Nobody on Ndeipi has that email address.");
        if (!await db.EventValidators.AnyAsync(v => v.EventId == id && v.UserId == user.Id, ct))
        {
            db.EventValidators.Add(new EventValidator { EventId = id, UserId = user.Id });
            await db.SaveChangesAsync(ct);
        }
        return new UserDto(user.Id, user.DisplayName, user.Username, user.AvatarUrl);
    }

    public async Task<bool> RemoveValidatorAsync(User me, Guid id, Guid userId, CancellationToken ct)
    {
        if (!await db.Events.AnyAsync(e => e.Id == id && e.OrganizerId == me.Id, ct))
            return false;
        await db.EventValidators.Where(v => v.EventId == id && v.UserId == userId).ExecuteDeleteAsync(ct);
        return true;
    }

    // ---- Buying ----

    /// <summary>Takes seats out of sale for <see cref="EventsContract.HoldDuration"/> (SRS FR-2.1).</summary>
    public async Task<HoldDto> HoldAsync(User me, Guid eventId, HoldRequest request, CancellationToken ct)
    {
        if (request.Quantity is < 1 or > EventsContract.MaxTicketsPerHold)
            throw new ChatRejectedException($"You can buy 1 to {EventsContract.MaxTicketsPerHold} tickets at a time.");
        var now = clock.GetUtcNow();
        var e = await db.Events.AsNoTracking().FirstOrDefaultAsync(e => e.Id == eventId && e.IsPublished, ct)
            ?? throw new ChatRejectedException("That event isn't on sale.");
        if (e.StartsAt < now)
            throw new ChatRejectedException("That event has already started.");
        var tier = await db.TicketTiers.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TierId && t.EventId == eventId, ct)
            ?? throw new ChatRejectedException("Pick a ticket type.");

        // One hold per person per event: a new one gives back the seats of the last.
        var previous = await db.TicketHolds.Where(h => h.UserId == me.Id && h.EventId == eventId && h.Status == HoldStatuses.Active)
            .Select(h => h.Id).ToListAsync(ct);
        foreach (var id in previous)
            await issuer.ReleaseAsync(id, HoldStatuses.Active, ct);

        var hold = new TicketHold
        {
            Id = Guid.NewGuid(),
            EventId = eventId,
            TierId = tier.Id,
            UserId = me.Id,
            Quantity = request.Quantity,
            Amount = tier.Price * request.Quantity,
            Status = HoldStatuses.Active,
            ExpiresAt = now + EventsContract.HoldDuration,
            CreatedAt = now
        };

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var taken = await db.TicketTiers
                .Where(t => t.Id == tier.Id && t.Capacity - t.Sold - t.Held >= request.Quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Held, t => t.Held + request.Quantity), ct);
            if (taken == 0)
                throw new ChatRejectedException(request.Quantity == 1 ? $"{tier.Name} is sold out." : $"There aren't {request.Quantity} {tier.Name} tickets left.");
            db.TicketHolds.Add(hold);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await issuer.BroadcastAsync(eventId, ct);
        return new HoldDto(hold.Id, eventId, tier.Id, tier.Name, hold.Quantity, Amounts.Format(hold.Amount), e.Currency, hold.ExpiresAt, hold.Status);
    }

    public async Task<bool> ReleaseAsync(User me, Guid holdId, CancellationToken ct)
    {
        if (!await db.TicketHolds.AnyAsync(h => h.Id == holdId && h.UserId == me.Id, ct))
            return false;
        await issuer.ReleaseAsync(holdId, HoldStatuses.Active, ct);
        return true;
    }

    /// <summary>
    /// Pays for a hold: free tickets are issued at once; paid ones become a Bridge transfer from the
    /// buyer's wallet to the organizer's, and are issued when it's confirmed (<see cref="TicketIssuer"/>).
    /// </summary>
    public async Task<OrderDto?> PayAsync(User me, Guid holdId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hold = await db.TicketHolds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.UserId == me.Id, ct);
        if (hold is null)
            return null;
        if (hold.Status != HoldStatuses.Active || hold.ExpiresAt <= now)
            throw new ChatRejectedException("This hold has expired; pick your tickets again.");
        var e = await db.Events.AsNoTracking().FirstAsync(e => e.Id == hold.EventId, ct);

        BankTransfer? transfer = null;
        if (hold.Amount > 0)
        {
            var options = bridge.Value;
            if (!options.IsConfigured)
                throw new ChatRejectedException("Paying for tickets isn't set up on this server.");
            if (e.OrganizerId == me.Id)
                throw new ChatRejectedException("You can't buy tickets to your own event.");
            if (!string.Equals(e.Currency, options.Currency, StringComparison.OrdinalIgnoreCase))
                throw new ChatRejectedException($"This event is priced in {e.Currency.ToUpperInvariant()}, which the wallet doesn't pay in.");
            if (hold.Amount > options.MaxTransferAmount)
                throw new ChatRejectedException($"The most you can pay at once is {Amounts.Format(options.MaxTransferAmount)} {e.Currency.ToUpperInvariant()}.");
            var profiles = await db.BankingProfiles.AsNoTracking()
                .Where(p => p.UserId == me.Id || p.UserId == e.OrganizerId)
                .ToListAsync(ct);
            if (profiles.FirstOrDefault(p => p.UserId == me.Id) is not { CanTransfer: true })
                throw new ChatRejectedException("Verify your identity under Me > Wallet before buying tickets.");
            if (profiles.FirstOrDefault(p => p.UserId == e.OrganizerId) is not { CanTransfer: true })
                throw new ChatRejectedException("The organizer can't take payments yet.");
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var paying = await db.TicketHolds
            .Where(h => h.Id == holdId && h.Status == HoldStatuses.Active && h.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatuses.Paying), ct);
        if (paying == 0)
            throw new ChatRejectedException("This hold has expired; pick your tickets again.");

        var order = new TicketOrder
        {
            Id = Guid.NewGuid(),
            HoldId = hold.Id,
            EventId = hold.EventId,
            TierId = hold.TierId,
            BuyerId = me.Id,
            Quantity = hold.Quantity,
            Amount = hold.Amount,
            Currency = e.Currency,
            Status = OrderStatuses.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TicketOrders.Add(order);
        if (hold.Amount > 0)
        {
            transfer = new BankTransfer
            {
                Id = Guid.NewGuid(),
                OrderId = order.Id,
                SenderId = me.Id,
                RecipientId = e.OrganizerId,
                Amount = hold.Amount,
                Currency = e.Currency,
                Memo = $"Tickets: {(e.Title.Length > 60 ? e.Title[..60] : e.Title)}",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.BankTransfers.Add(transfer);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (transfer is null)
            await issuer.CompleteAsync(order.Id, ct);
        else
            await banking.SubmitTransferAsync(transfer.Id, ct);

        return await OrderAsync(me, order.Id, ct);
    }

    public async Task<OrderDto?> OrderAsync(User me, Guid orderId, CancellationToken ct)
    {
        var order = await db.TicketOrders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.BuyerId == me.Id, ct);
        if (order is null)
            return null;
        var tickets = await TicketsQuery(t => t.OrderId == orderId).ToListAsync(ct);
        return new OrderDto(order.Id, order.EventId, order.Status, Amounts.Format(order.Amount), order.Currency, order.Error, tickets);
    }

    // ---- Tickets ----

    public Task<List<TicketDto>> TicketsAsync(User me, CancellationToken ct) =>
        TicketsQuery(t => t.OwnerId == me.Id).ToListAsync(ct);

    IQueryable<TicketDto> TicketsQuery(System.Linq.Expressions.Expression<Func<Ticket, bool>> filter) =>
        db.Tickets.AsNoTracking().Where(filter)
            .Join(db.Events, t => t.EventId, e => e.Id, (t, e) => new { t, e })
            .Join(db.TicketTiers, x => x.t.TierId, tier => tier.Id, (x, tier) => new { x.t, x.e, tier })
            .OrderBy(x => x.e.StartsAt).ThenBy(x => x.t.CreatedAt).ThenBy(x => x.t.Id)
            .Select(x => new TicketDto(x.t.Id, x.e.Id, x.e.Title, x.tier.Name, x.e.StartsAt, x.e.Venue, x.e.City, x.t.Status, x.t.HolderPublicKey));

    /// <summary>Registers the public key of the device that will show this ticket's QR codes.</summary>
    public async Task<bool> BindHolderKeyAsync(User me, Guid ticketId, string? publicKeySpki, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId && t.OwnerId == me.Id, ct);
        if (ticket is null)
            return false;
        if (ticket.Status != TicketStatuses.Valid)
            throw new ChatRejectedException("This ticket has already been used.");
        ticket.HolderPublicKey = NormalizeP256Key(publicKeySpki);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public static string NormalizeP256Key(string? spkiBase64)
    {
        try
        {
            var spki = Convert.FromBase64String(spkiBase64 ?? "");
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(spki, out var read);
            if (read != spki.Length || key.KeySize != 256)
                throw new CryptographicException();
            return Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new ChatRejectedException("That isn't a P-256 public key.");
        }
    }

    // ---- Gate ----

    public Task<bool> CanScanAsync(User me, Guid eventId, CancellationToken ct) =>
        db.Events.AnyAsync(e => e.Id == eventId && (e.OrganizerId == me.Id || db.EventValidators.Any(v => v.EventId == eventId && v.UserId == me.Id)), ct);

    /// <summary>Everything a gate needs to admit people with no signal (SRS FR-5.2): public keys only.</summary>
    public async Task<GateListDto?> GateListAsync(User me, Guid eventId, CancellationToken ct)
    {
        if (!await CanScanAsync(me, eventId, ct))
            return null;
        var title = await db.Events.Where(e => e.Id == eventId).Select(e => e.Title).FirstAsync(ct);
        var tickets = await db.Tickets.AsNoTracking().Where(t => t.EventId == eventId)
            .Join(db.TicketTiers, t => t.TierId, tier => tier.Id, (t, tier) => new { t, tier })
            .Join(db.Users, x => x.t.OwnerId, u => u.Id, (x, u) => new GateTicketDto(x.t.Id, x.t.HolderPublicKey, x.t.Status, x.tier.Name, u.DisplayName, x.t.AdmittedAt))
            .ToListAsync(ct);
        return new GateListDto(eventId, title, clock.GetUtcNow(), tickets);
    }

    public const int MaxAdmissionsPerUpload = 500;

    /// <summary>
    /// Records admissions a gate made (possibly offline). The first to reach the server wins; a
    /// ticket admitted twice comes back AlreadyAdmitted with when it first got in.
    /// </summary>
    public async Task<IReadOnlyList<AdmissionResultDto>?> AdmitAsync(User me, Guid eventId, AdmissionsRequest request, CancellationToken ct)
    {
        if (!await CanScanAsync(me, eventId, ct))
            return null;
        var admissions = request.Admissions ?? [];
        if (admissions.Count > MaxAdmissionsPerUpload)
            throw new ChatRejectedException($"Upload up to {MaxAdmissionsPerUpload} admissions at a time.");
        var device = request.DeviceName?.Trim() is { Length: > 0 } d ? d[..Math.Min(d.Length, 80)] : "gate";
        var by = $"{me.DisplayName[..Math.Min(me.DisplayName.Length, 36)]} ({device})";
        var now = clock.GetUtcNow();

        var results = new List<AdmissionResultDto>();
        foreach (var a in admissions.DistinctBy(a => a.TicketId))
        {
            // A gate's clock can't put an admission in the future.
            var at = a.AdmittedAt > now ? now : a.AdmittedAt;
            var admitted = await db.Tickets
                .Where(t => t.Id == a.TicketId && t.EventId == eventId && t.Status == TicketStatuses.Valid)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Status, TicketStatuses.Admitted)
                    .SetProperty(t => t.AdmittedAt, at)
                    .SetProperty(t => t.AdmittedBy, by), ct);
            if (admitted == 1)
            {
                results.Add(new AdmissionResultDto(a.TicketId, AdmissionOutcomes.Admitted, at));
                continue;
            }
            var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == a.TicketId && t.EventId == eventId, ct);
            results.Add(ticket switch
            {
                null => new AdmissionResultDto(a.TicketId, AdmissionOutcomes.Unknown, null),
                { Status: TicketStatuses.Admitted } => new AdmissionResultDto(a.TicketId, AdmissionOutcomes.AlreadyAdmitted, ticket.AdmittedAt),
                _ => new AdmissionResultDto(a.TicketId, AdmissionOutcomes.Revoked, null)
            });
        }
        return results;
    }

    // ---- Mapping ----

    static EventSummaryDto ToSummary(EventListing e) => new(
        e.Id, e.Title, e.Category, e.City, e.Venue, e.StartsAt, e.Currency,
        e.Tiers.Count == 0 ? null : Amounts.Format(e.Tiers.Min(t => t.Price)),
        e.Tiers.Sum(Available));

    static EventDetailDto ToDetail(EventListing e, bool canManage, bool canScan) => new(
        e.Id, e.Title, e.Description, e.Category, e.City, e.Venue, e.StartsAt, e.Currency,
        e.Tiers.OrderBy(t => t.Position).Select(t => new TicketTierDto(t.Id, t.Name, Amounts.Format(t.Price), t.Capacity, Available(t))).ToList(),
        new UserDto(e.Organizer.Id, e.Organizer.DisplayName, e.Organizer.Username, e.Organizer.AvatarUrl),
        e.IsPublished, canManage, canScan);

    public static int Available(TicketTier t) => Math.Max(0, t.Capacity - t.Sold - t.Held);
}

/// <summary>
/// Moves seats between held, sold and free again, and issues tickets. Used by checkout, by the
/// Bridge transfer listener (payments confirm long after the request) and by the hold sweeper.
/// </summary>
public sealed class TicketIssuer(ChatDbContext db, ChatNotifier notifier, TimeProvider clock, ILogger<TicketIssuer> log)
{
    /// <summary>Gives a hold's seats back, if it's still <paramref name="fromStatus"/>.</summary>
    public async Task ReleaseAsync(Guid holdId, string fromStatus, CancellationToken ct)
    {
        var hold = await db.TicketHolds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId, ct);
        if (hold is null)
            return;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var released = await db.TicketHolds.Where(h => h.Id == holdId && h.Status == fromStatus)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatuses.Released), ct);
            if (released == 0)
                return;
            await db.TicketTiers.Where(t => t.Id == hold.TierId)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Held, t => t.Held - hold.Quantity), ct);
            await tx.CommitAsync(ct);
        }
        await BroadcastAsync(hold.EventId, ct);
    }

    /// <summary>The order is paid: its held seats become sold and its tickets exist. Idempotent.</summary>
    public async Task CompleteAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.TicketOrders.AsNoTracking().FirstAsync(o => o.Id == orderId, ct);
        var now = clock.GetUtcNow();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var paid = await db.TicketOrders.Where(o => o.Id == orderId && o.Status == OrderStatuses.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatuses.Paid).SetProperty(o => o.UpdatedAt, now), ct);
            if (paid == 0)
                return;
            await db.TicketHolds.Where(h => h.Id == order.HoldId)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HoldStatuses.Converted), ct);
            await db.TicketTiers.Where(t => t.Id == order.TierId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.Held, t => t.Held - order.Quantity)
                    .SetProperty(t => t.Sold, t => t.Sold + order.Quantity), ct);
            for (var i = 0; i < order.Quantity; i++)
                db.Tickets.Add(new Ticket
                {
                    Id = Guid.NewGuid(),
                    OrderId = order.Id,
                    EventId = order.EventId,
                    TierId = order.TierId,
                    OwnerId = order.BuyerId,
                    Status = TicketStatuses.Valid,
                    CreatedAt = now
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        log.LogInformation("Issued {Count} tickets for order {OrderId}", order.Quantity, orderId);
        await BroadcastAsync(order.EventId, ct);
    }

    /// <summary>The payment failed: the order says why and the seats go back on sale.</summary>
    public async Task FailAsync(Guid orderId, string? error, CancellationToken ct)
    {
        var order = await db.TicketOrders.AsNoTracking().FirstAsync(o => o.Id == orderId, ct);
        var failed = await db.TicketOrders.Where(o => o.Id == orderId && o.Status == OrderStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(o => o.Status, OrderStatuses.Failed)
                .SetProperty(o => o.Error, error ?? "The payment didn't go through.")
                .SetProperty(o => o.UpdatedAt, clock.GetUtcNow()), ct);
        if (failed == 1)
            await ReleaseAsync(order.HoldId, HoldStatuses.Paying, ct);
    }

    /// <summary>Tells everyone looking at the event how many seats are left (SRS FR-1.3).</summary>
    public async Task BroadcastAsync(Guid eventId, CancellationToken ct)
    {
        var tiers = await db.TicketTiers.AsNoTracking().Where(t => t.EventId == eventId)
            .Select(t => new TierAvailabilityDto(t.Id, t.Capacity - t.Sold - t.Held))
            .ToListAsync(ct);
        await notifier.PublishAsync(EventsContract.Topic(eventId), new EventAvailabilityDto(eventId, tiers));
    }
}

/// <summary>Issues tickets when their Bridge payment is confirmed, and frees the seats when it fails.</summary>
public sealed class TicketPaymentListener(TicketIssuer issuer) : IBankTransferListener
{
    public Task TransferChangedAsync(BankTransfer transfer, CancellationToken ct) =>
        (transfer.OrderId, transfer.Status) switch
        {
            ({ } orderId, TransferStatuses.Confirmed) => issuer.CompleteAsync(orderId, ct),
            ({ } orderId, TransferStatuses.Failed) => issuer.FailAsync(orderId, transfer.Error, ct),
            _ => Task.CompletedTask
        };
}

/// <summary>Anyone may follow a published event's availability; its organizer and validators, any of theirs.</summary>
public sealed class EventTopicPolicy(ChatDbContext db) : ITopicPolicy
{
    public string Prefix => "events";

    public async Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Guid.TryParseExact(key, "N", out var id)
        && await db.Events.AnyAsync(e => e.Id == id && (e.IsPublished || e.OrganizerId == user.Id || db.EventValidators.Any(v => v.EventId == id && v.UserId == user.Id)), ct);
}

/// <summary>Puts seats from abandoned holds back on sale once their ten minutes are up.</summary>
public sealed class TicketHoldSweeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<TicketHoldSweeper> log) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, clock);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(scopes, clock, stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    log.LogError(ex, "Ticket hold sweep failed");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    public static async Task<int> SweepAsync(IServiceScopeFactory scopes, TimeProvider clock, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var issuer = scope.ServiceProvider.GetRequiredService<TicketIssuer>();
        var now = clock.GetUtcNow();
        var expired = await db.TicketHolds.Where(h => h.Status == HoldStatuses.Active && h.ExpiresAt <= now)
            .Select(h => h.Id).Take(200).ToListAsync(ct);
        foreach (var id in expired)
            await issuer.ReleaseAsync(id, HoldStatuses.Active, ct);
        return expired.Count;
    }
}
