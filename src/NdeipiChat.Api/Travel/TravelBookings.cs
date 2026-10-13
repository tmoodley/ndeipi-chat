using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Points;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Travel;

/// <summary>
/// Booking on Lets Travel: holding a room or seats for 15 minutes (FR-2.3), paying from the Ndeipi
/// wallet with Ndeipi's fee (FR-4.2), Believe Points off that fee (FR-4.3), and the booking's life
/// after: confirmation, the chat with the vendor (FR-5.3), refunds, rescheduled departures (FR-3.3)
/// and check-in from the offline voucher (FR-5.1).
///
/// Rooms are counted per night and seats per departure. A hold takes them with one conditional
/// UPDATE that only succeeds while enough are free, so however many people try for the last room at
/// once, exactly one gets it (NFR-2, acceptance 1).
/// </summary>
public sealed class TravelBookings(
    ChatDbContext db,
    TravelCatalog catalog,
    TravelIssuer issuer,
    BankingService banking,
    PointsService points,
    IDataProtectionProvider protection,
    IOptions<TravelOptions> options,
    IOptions<BridgeOptions> bridge,
    TimeProvider clock)
{
    public const string Active = "active", Paying = "paying", Converted = "converted", Released = "released";

    DateTimeOffset Now => clock.GetUtcNow();

    IDataProtector Ids => protection.CreateProtector("travel-id-numbers");

    // ------------------------------------------------------------------ holds (FR-2.3)

    public async Task<TravelHoldDto> HoldAsync(User me, TravelHoldRequest r, CancellationToken ct)
    {
        var quantity = r.Quantity;
        TravelHold hold;
        if (r.Type == TravelItemTypes.Stay)
        {
            var (checkIn, checkOut) = catalog.ValidateDates(r.CheckIn, r.CheckOut, required: true);
            if (quantity is < 1 or > TravelContract.MaxRoomsPerBooking)
                throw new ChatRejectedException($"Book 1 to {TravelContract.MaxRoomsPerBooking} rooms at a time.");
            var room = await db.RoomTypes.AsNoTracking().FirstOrDefaultAsync(t => t.Id == r.ItemId && !t.Archived, ct)
                ?? throw new ChatRejectedException("That room isn't on offer.");
            var property = await BookablePropertyAsync(me, room.PropertyId, ct);
            if (r.Guests < 1 || r.Guests > room.Capacity * quantity)
                throw new ChatRejectedException($"{quantity} {room.Name} room{(quantity == 1 ? "" : "s")} sleep{(quantity == 1 ? "s" : "")} up to {room.Capacity * quantity}.");

            await ReleaseMineAsync(me, ct);
            var nights = await catalog.NightsAsync([room.Id], checkIn!.Value, checkOut!.Value, ct);
            var prices = TravelCatalog.Stay(room, checkIn.Value, checkOut.Value, nights).Prices;
            var lines = StayLines(room, prices, quantity);
            hold = NewHold(me, TravelItemTypes.Stay, room.Id, property.VendorId, quantity, r.Guests, lines);
            (hold.CheckIn, hold.CheckOut) = (checkIn, checkOut);

            for (var d = checkIn.Value; d < checkOut.Value; d = d.AddDays(1))
                await catalog.EnsureNightAsync(room.Id, d, ct);
            await TakeAsync(hold, async () =>
            {
                var count = checkOut.Value.DayNumber - checkIn.Value.DayNumber;
                var roomId = room.Id;
                var taken = await db.RoomNights
                    .Where(n => n.RoomTypeId == roomId && n.Date >= checkIn.Value && n.Date < checkOut.Value && !n.Closed
                                && db.RoomTypes.Where(t => t.Id == roomId).Select(t => t.Units).First() - n.Held - n.Booked >= quantity)
                    .ExecuteUpdateAsync(s => s.SetProperty(n => n.Held, n => n.Held + quantity), ct);
                return taken == count;
            }, quantity == 1 ? $"{room.Name} is fully booked for those dates." : $"There aren't {quantity} {room.Name} rooms free for those dates.", ct);
        }
        else if (r.Type == TravelItemTypes.Tour)
        {
            if (quantity is < 1 or > TravelContract.MaxGuestsPerTour)
                throw new ChatRejectedException($"Book 1 to {TravelContract.MaxGuestsPerTour} people at a time.");
            var departure = await db.TourDepartures.AsNoTracking().FirstOrDefaultAsync(d => d.Id == r.ItemId, ct)
                ?? throw new ChatRejectedException("That departure isn't on offer.");
            if (departure.Status == DepartureStatuses.Canceled || departure.StartsAt <= Now)
                throw new ChatRejectedException("That departure isn't taking bookings.");
            var tour = await BookableTourAsync(me, departure.TourId, ct);
            var addOns = await db.TourAddOns.AsNoTracking().Where(a => a.TourId == tour.Id && !a.Archived).ToListAsync(ct);
            var chosen = r.AddOnIds ?? [];
            if (chosen.Any(id => addOns.All(a => a.Id != id)))
                throw new ChatRejectedException("One of those extras isn't offered.");
            var lines = new List<TravelLineDto> { new($"{quantity} × {tour.Title}", tour.PricePerPerson * quantity) };
            foreach (var a in addOns.Where(a => a.Mandatory || chosen.Contains(a.Id)).OrderByDescending(a => a.Mandatory).ThenBy(a => a.Name))
                lines.Add(new TravelLineDto(a.PerPerson ? $"{a.Name} × {quantity}" : a.Name, a.PerPerson ? a.Price * quantity : a.Price));

            await ReleaseMineAsync(me, ct);
            hold = NewHold(me, TravelItemTypes.Tour, departure.Id, tour.VendorId, quantity, quantity, lines);
            await TakeAsync(hold, async () => await db.TourDepartures
                .Where(d => d.Id == departure.Id && d.Status != DepartureStatuses.Canceled && d.Seats - d.Held - d.Booked >= quantity)
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Held, d => d.Held + quantity), ct) == 1,
                quantity == 1 ? "That departure is full." : $"There aren't {quantity} seats left on that departure.", ct);
        }
        else
            throw new ChatRejectedException("Book a stay or a tour.");

        await issuer.NotifyAsync([me.Id], null, ct);
        return await HoldDtoAsync(me, hold, ct);
    }

    /// <summary>Takes the room nights or seats, and saves the hold, in one transaction; a deadlock between racing holds is retried.</summary>
    async Task TakeAsync(TravelHold hold, Func<Task<bool>> take, string soldOut, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                if (!await take())
                    throw new ChatRejectedException(soldOut);
                db.TravelHolds.Add(hold);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return;
            }
            catch (Exception e) when (attempt < 3 && (e is SqlException { Number: 1205 } || e.InnerException is SqlException { Number: 1205 }))
            {
                db.ChangeTracker.Clear();
            }
        }
    }

    TravelHold NewHold(User me, string type, Guid referenceId, Guid vendorId, int quantity, int guests, List<TravelLineDto> lines) => new()
    {
        Id = Guid.NewGuid(),
        UserId = me.Id,
        ItemType = type,
        ReferenceId = referenceId,
        VendorId = vendorId,
        Quantity = quantity,
        Guests = guests,
        LinesJson = JsonSerializer.Serialize(lines, ContractJson.Options),
        Amount = lines.Sum(l => l.Amount),
        Status = Active,
        ExpiresAt = Now + TravelContract.HoldDuration,
        CreatedAt = Now
    };

    static List<TravelLineDto> StayLines(RoomType room, IReadOnlyList<decimal> prices, int quantity)
    {
        var nights = prices.Count;
        var label = $"{quantity} × {room.Name}, {nights} night{(nights == 1 ? "" : "s")}";
        if (prices.Distinct().Count() == 1)
            label += $" at {TravelContract.Money(prices[0])}";
        return [new TravelLineDto(label, prices.Sum() * quantity)];
    }

    async Task<TravelProperty> BookablePropertyAsync(User me, Guid propertyId, CancellationToken ct)
    {
        var p = await db.TravelProperties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == propertyId && p.Status == TravelCatalog.Published, ct)
            ?? throw new ChatRejectedException("That property isn't taking bookings.");
        await BookableVendorAsync(me, p.VendorId, ct);
        return p;
    }

    async Task<TourPackage> BookableTourAsync(User me, Guid tourId, CancellationToken ct)
    {
        var t = await db.TourPackages.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tourId && t.Status == TravelCatalog.Published, ct)
            ?? throw new ChatRejectedException("That tour isn't taking bookings.");
        await BookableVendorAsync(me, t.VendorId, ct);
        return t;
    }

    async Task BookableVendorAsync(User me, Guid vendorId, CancellationToken ct)
    {
        if (vendorId == me.Id)
            throw new ChatRejectedException("You can't book your own listing.");
        if (!await db.TravelVendors.AnyAsync(v => v.UserId == vendorId && v.Status == TravelCatalog.Active, ct))
            throw new ChatRejectedException("This vendor isn't taking bookings right now.");
    }

    /// <summary>One checkout at a time: a new hold gives back what the last one held.</summary>
    async Task ReleaseMineAsync(User me, CancellationToken ct)
    {
        foreach (var id in await db.TravelHolds.Where(h => h.UserId == me.Id && h.Status == Active).Select(h => h.Id).ToListAsync(ct))
            await issuer.ReleaseAsync(id, Active, ct);
    }

    public async Task<bool> ReleaseAsync(User me, Guid holdId, CancellationToken ct)
    {
        if (!await db.TravelHolds.AnyAsync(h => h.Id == holdId && h.UserId == me.Id, ct))
            return false;
        await issuer.ReleaseAsync(holdId, Active, ct);
        return true;
    }

    public async Task<TravelHoldDto?> GetHoldAsync(User me, Guid holdId, CancellationToken ct) =>
        await db.TravelHolds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.UserId == me.Id, ct) is { } hold ? await HoldDtoAsync(me, hold, ct) : null;

    async Task<TravelHoldDto> HoldDtoAsync(User me, TravelHold h, CancellationToken ct)
    {
        var (title, subtitle, startsAt) = await DescribeAsync(h.ItemType, h.ReferenceId, ct);
        var lines = JsonSerializer.Deserialize<List<TravelLineDto>>(h.LinesJson, ContractJson.Options)!;
        var balance = (await points.GetAsync(me.Id, ct)).Balance;
        return new TravelHoldDto(h.Id, h.ItemType, h.ReferenceId, title, subtitle, h.CheckIn, h.CheckOut, startsAt, h.Quantity, h.Guests, lines, h.Amount,
            "USD", h.ExpiresAt, h.Status, Approximations(h.Amount), FeeOf(h.Amount), balance, options.Value.PointsPerDollar);
    }

    /// <summary>FR-4.1: the total in other currencies, at indicative rates, for reading only.</summary>
    IReadOnlyList<TravelLineDto> Approximations(decimal usd) =>
        [.. options.Value.DisplayRates.OrderBy(r => r.Key).Select(r => new TravelLineDto(r.Key, decimal.Round(usd * r.Value, 2)))];

    decimal FeeOf(decimal amount) => decimal.Round(amount * options.Value.FeePercent / 100m, 2, MidpointRounding.AwayFromZero);

    /// <summary>What a hold or booking item is: the property and room, or the tour and its departure.</summary>
    async Task<(string Title, string Subtitle, DateTimeOffset? StartsAt)> DescribeAsync(string type, Guid referenceId, CancellationToken ct)
    {
        if (type == TravelItemTypes.Stay)
        {
            var x = await db.RoomTypes.AsNoTracking().Where(t => t.Id == referenceId)
                .Join(db.TravelProperties, t => t.PropertyId, p => p.Id, (t, p) => new { p.Name, Room = t.Name, t.BoardPlan, p.City })
                .FirstAsync(ct);
            return (x.Name, $"{x.Room} · {TravelContract.Label(TravelContract.BoardPlans, x.BoardPlan)} · {x.City}", null);
        }
        var y = await db.TourDepartures.AsNoTracking().Where(d => d.Id == referenceId)
            .Join(db.TourPackages, d => d.TourId, t => t.Id, (d, t) => new { t.Title, t.City, d.StartsAt, t.DurationHours })
            .FirstAsync(ct);
        return (y.Title, $"{y.City} · {Hours(y.DurationHours)}", y.StartsAt);
    }

    static string Hours(double h) => h >= 24 ? $"{Math.Round(h / 24, 1)} days" : $"{h:0.#} hours";

    // ------------------------------------------------------------------ paying (FR-4)

    /// <summary>
    /// Pays for a hold from the wallet: a transfer to the vendor with Ndeipi's fee taken by Bridge,
    /// less any Believe Points discount (which comes out of that fee). The booking is confirmed when the
    /// transfer is (<see cref="TravelIssuer"/>); if it fails, the room or seats go back on sale.
    /// </summary>
    public async Task<TravelBookingDto> CheckoutAsync(User me, Guid holdId, TravelCheckoutRequest r, CancellationToken ct)
    {
        var hold = await db.TravelHolds.AsNoTracking().FirstOrDefaultAsync(h => h.Id == holdId && h.UserId == me.Id, ct)
            ?? throw new ChatRejectedException("That hold isn't yours.");
        if (await db.TravelBookings.AsNoTracking().FirstOrDefaultAsync(b => b.HoldId == holdId, ct) is { } existing)
            return (await issuer.DtosAsync(me, [existing], ct))[0];
        if (hold.Status != Active || hold.ExpiresAt <= Now)
            throw new ChatRejectedException("Your 15 minutes ran out and the booking was released. Choose it again.");

        var lead = r.LeadGuestName?.Trim() is { Length: > 1 and <= TravelContract.MaxNameLength } name ? name : throw new ChatRejectedException("Enter the lead guest's full name.");
        var wallet = bridge.Value;
        if (!wallet.IsConfigured)
            throw new ChatRejectedException("Payments aren't set up on this server.");
        if (hold.Amount > wallet.MaxTransferAmount)
            throw new ChatRejectedException($"The most you can pay at once is {TravelContract.Money(wallet.MaxTransferAmount)}.");
        var profiles = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == me.Id || p.UserId == hold.VendorId).ToListAsync(ct);
        if (profiles.FirstOrDefault(p => p.UserId == me.Id) is not { CanTransfer: true })
            throw new ChatRejectedException("Verify your identity under Me > Wallet before booking.");
        if (profiles.FirstOrDefault(p => p.UserId == hold.VendorId) is not { CanTransfer: true })
            throw new ChatRejectedException("This vendor can't take payments right now.");

        var fee = FeeOf(hold.Amount);
        var (discount, spent) = (0m, 0);
        if (r.RedeemPoints && fee > 0)
        {
            var balance = (await points.GetAsync(me.Id, ct)).Balance;
            discount = Math.Min(fee, Math.Floor(balance * 100m / options.Value.PointsPerDollar) / 100m);
            spent = (int)Math.Ceiling(discount * options.Value.PointsPerDollar);
        }

        var booking = new TravelBooking
        {
            Id = Guid.NewGuid(),
            Reference = NewReference(),
            TravelerId = me.Id,
            VendorId = hold.VendorId,
            HoldId = hold.Id,
            Status = TravelBookingStatuses.PendingPayment,
            Amount = hold.Amount,
            Fee = fee,
            Discount = discount,
            PointsRedeemed = spent,
            Currency = "USD",
            LeadGuestName = lead,
            Phone = r.Phone?.Trim() is { Length: > 0 and <= 40 } phone ? phone : null,
            IdNumberProtected = r.IdNumber?.Trim() is { Length: > 0 and <= 40 } id ? Ids.Protect(id) : null,
            SpecialRequests = r.SpecialRequests?.Trim() is { Length: > 0 } s ? s[..Math.Min(s.Length, TravelContract.MaxRequestsLength)] : null,
            CreatedAt = Now
        };
        var (title, subtitle, startsAt) = await DescribeAsync(hold.ItemType, hold.ReferenceId, ct);
        var listingId = hold.ItemType == TravelItemTypes.Stay
            ? await db.RoomTypes.Where(t => t.Id == hold.ReferenceId).Select(t => t.PropertyId).FirstAsync(ct)
            : await db.TourDepartures.Where(d => d.Id == hold.ReferenceId).Select(d => d.TourId).FirstAsync(ct);
        var transfer = new BankTransfer
        {
            Id = Guid.NewGuid(),
            SenderId = me.Id,
            RecipientId = hold.VendorId,
            // The traveller pays the price less the discount; Bridge keeps the fee less the discount; the vendor gets the price less the full fee.
            Amount = hold.Amount - discount,
            Currency = wallet.Currency,
            Memo = $"Lets Travel {booking.Reference}: {(title.Length > 80 ? title[..80] : title)}",
            FeeAmount = fee - discount,
            CreatedAt = Now,
            UpdatedAt = Now
        };
        booking.BankTransferId = transfer.Id;

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var paying = await db.TravelHolds.Where(h => h.Id == holdId && h.Status == Active && h.ExpiresAt > Now)
                .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, Paying), ct);
            if (paying == 0)
                throw new ChatRejectedException("Your 15 minutes ran out and the booking was released. Choose it again.");
            db.TravelBookings.Add(booking);
            db.TravelBookingItems.Add(new TravelBookingItem
            {
                Id = Guid.NewGuid(),
                BookingId = booking.Id,
                ItemType = hold.ItemType,
                ReferenceId = hold.ReferenceId,
                ListingId = listingId,
                Title = title,
                Subtitle = subtitle,
                CheckIn = hold.CheckIn,
                CheckOut = hold.CheckOut,
                StartsAt = startsAt,
                Quantity = hold.Quantity,
                Guests = hold.Guests,
                LinesJson = hold.LinesJson,
                Amount = hold.Amount
            });
            db.BankTransfers.Add(transfer);
            if (spent > 0)
                db.Points.Add(new PointsEntry
                {
                    UserId = me.Id, Points = -spent, Source = "travel", SourceId = booking.Id.ToString("N"),
                    Description = $"Lets Travel {booking.Reference}: {TravelContract.Money(discount)} off", CreatedAt = Now
                });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        await banking.SubmitTransferAsync(transfer.Id, ct);
        var fresh = await db.TravelBookings.AsNoTracking().FirstAsync(b => b.Id == booking.Id, ct);
        return (await issuer.DtosAsync(me, [fresh], ct))[0];
    }

    static string NewReference()
    {
        const string alphabet = "ABCDEFGHJKMNPQRSTVWXYZ23456789";
        return "LT-" + string.Concat(Enumerable.Range(0, 6).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]));
    }

    // ------------------------------------------------------------------ bookings

    public async Task<IReadOnlyList<TravelBookingDto>> MineAsync(User me, CancellationToken ct) =>
        await issuer.DtosAsync(me, await db.TravelBookings.AsNoTracking().Where(b => b.TravelerId == me.Id).OrderByDescending(b => b.CreatedAt).Take(100).ToListAsync(ct), ct);

    public async Task<TravelBookingDto?> GetAsync(User me, Guid id, CancellationToken ct)
    {
        var b = await db.TravelBookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && (b.TravelerId == me.Id || b.VendorId == me.Id || TravelCatalog.IsAdmin(me)), ct);
        return b is null ? null : (await issuer.DtosAsync(me, [b], ct))[0];
    }

    public async Task<VendorDashboardDto> DashboardAsync(User me, CancellationToken ct)
    {
        var vendor = await db.TravelVendors.AsNoTracking().FirstOrDefaultAsync(v => v.UserId == me.Id, ct) ?? throw new ChatRejectedException("Set up your vendor profile first.");
        var bookings = await db.TravelBookings.AsNoTracking().Where(b => b.VendorId == me.Id && b.Status != TravelBookingStatuses.Failed && b.Status != TravelBookingStatuses.PendingPayment)
            .OrderByDescending(b => b.CreatedAt).Take(200).ToListAsync(ct);
        var monthStart = new DateTimeOffset(Now.Year, Now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var month = bookings.Where(b => b.ConfirmedAt >= monthStart && b.Status != TravelBookingStatuses.Canceled).ToList();
        return new VendorDashboardDto(
            new TravelVendorDto(vendor.UserId, vendor.Name, vendor.Description, vendor.Phone, vendor.Email, vendor.Status, (int)options.Value.FeePercent),
            await catalog.ListingsAsync(me.Id, ct), await issuer.DtosAsync(me, bookings, ct), month.Sum(b => b.Amount - b.Fee), month.Sum(b => b.Fee));
    }

    /// <summary>The vendor cancels a confirmed booking and refunds in full from their wallet; the room or seats go back on sale.</summary>
    public async Task<TravelBookingDto> CancelByVendorAsync(User me, Guid id, string? reason, CancellationToken ct)
    {
        var b = await db.TravelBookings.AsNoTracking().FirstOrDefaultAsync(b => b.Id == id && b.VendorId == me.Id, ct) ?? throw new ChatRejectedException("That booking isn't yours.");
        if (b.Status != TravelBookingStatuses.Confirmed)
            throw new ChatRejectedException(b.Status == TravelBookingStatuses.CheckedIn ? "This guest has already checked in." : "Only a confirmed booking can be cancelled.");
        var note = reason?.Trim() is { Length: > 0 } text ? text[..Math.Min(text.Length, 300)] : "Cancelled by the vendor.";
        await issuer.CancelAndRefundAsync(b.Id, me.Id, note, ct);
        return (await issuer.DtosAsync(me, [await db.TravelBookings.AsNoTracking().FirstAsync(x => x.Id == id, ct)], ct))[0];
    }

    /// <summary>
    /// FR-3.3: the vendor moves a departure (weather, a breakdown) or calls it off. Everyone booked on it
    /// is told live and in their chat with the vendor; a cancelled departure is refunded in full.
    /// </summary>
    public async Task<DepartureDto> RescheduleAsync(User me, Guid tourId, Guid departureId, RescheduleRequest r, CancellationToken ct)
    {
        await catalog.OwnTourAsync(me, tourId, ct);
        var reason = r.Reason?.Trim() is { Length: > 2 } text ? text[..Math.Min(text.Length, 300)] : throw new ChatRejectedException("Say why, for the travellers.");
        var d = await db.TourDepartures.FirstOrDefaultAsync(x => x.Id == departureId && x.TourId == tourId, ct) ?? throw new ChatRejectedException("That departure isn't here.");
        if (d.Status == DepartureStatuses.Canceled)
            throw new ChatRejectedException("That departure was already called off.");
        var old = d.StartsAt;
        if (r.Cancel)
            (d.Status, d.Note) = (DepartureStatuses.Canceled, reason);
        else
        {
            if (r.StartsAt is not { } when || when <= Now)
                throw new ChatRejectedException("Choose the new departure time.");
            (d.StartsAt, d.Status, d.Note) = (when, DepartureStatuses.Rescheduled, reason);
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ChatRejectedException("This departure changed at the same moment. Reload and try again.");
        }
        await issuer.DepartureChangedAsync(d.Id, old, ct);
        return TravelCatalog.ToDto(d);
    }

    // ------------------------------------------------------------------ vouchers and check-in (FR-5.1)

    /// <summary>Registers the device key that signs this booking's offline voucher QR.</summary>
    public async Task<bool> BindKeyAsync(User me, Guid id, string? publicKey, CancellationToken ct)
    {
        var b = await db.TravelBookings.FirstOrDefaultAsync(b => b.Id == id && b.TravelerId == me.Id, ct);
        if (b is null)
            return false;
        b.HolderPublicKey = Events.EventsService.NormalizeP256Key(publicKey);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Checks a guest in from what the vendor scanned: the voucher's rolling QR (signed on the guest's
    /// phone, working offline), the printed voucher's code, or the booking reference typed in.
    /// </summary>
    public async Task<CheckInResultDto> CheckInAsync(User me, string? code, CancellationToken ct)
    {
        code = code?.Trim() ?? "";
        TravelBooking? b = null;
        if (TravelContract.TryParseQr(code, out var bookingId, out var window, out var signature))
        {
            b = await db.TravelBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == bookingId, ct);
            var now = Now.ToUnixTimeSeconds() / TravelContract.QrWindowSeconds;
            if (b?.HolderPublicKey is not { } key || Math.Abs(now - window) > TravelContract.QrWindowTolerance || !Verify(key, TravelContract.QrPayload(bookingId, window), signature))
                return new CheckInResultDto(CheckInOutcomes.Invalid, null, "This code isn't valid. Ask the guest to reopen their voucher.");
        }
        else if (TravelIssuer.TryReadPrinted(protection, code) is { } printed)
            b = await db.TravelBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Id == printed, ct);
        else if (code.StartsWith("LT-", StringComparison.OrdinalIgnoreCase))
            b = await db.TravelBookings.AsNoTracking().FirstOrDefaultAsync(x => x.Reference == code.ToUpperInvariant() && x.VendorId == me.Id, ct);

        if (b is null)
            return new CheckInResultDto(CheckInOutcomes.Invalid, null, "No booking matches that code.");
        if (b.VendorId != me.Id)
            return new CheckInResultDto(CheckInOutcomes.NotYours, null, "This booking is with another vendor.");
        var dto = (await issuer.DtosAsync(me, [b], ct))[0];
        if (b.Status == TravelBookingStatuses.CheckedIn)
            return new CheckInResultDto(CheckInOutcomes.AlreadyCheckedIn, dto, $"Already checked in {b.CheckedInAt:d MMM HH:mm} by {b.CheckedInBy}.");
        if (b.Status != TravelBookingStatuses.Confirmed)
            return new CheckInResultDto(CheckInOutcomes.Canceled, dto, "This booking isn't confirmed (it was cancelled or never paid).");

        var by = me.DisplayName.Length > 100 ? me.DisplayName[..100] : me.DisplayName;
        var done = await db.TravelBookings.Where(x => x.Id == b.Id && x.Status == TravelBookingStatuses.Confirmed)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, TravelBookingStatuses.CheckedIn).SetProperty(x => x.CheckedInAt, Now).SetProperty(x => x.CheckedInBy, by), ct);
        await issuer.NotifyAsync([b.TravelerId, b.VendorId], new TravelChangedDto(b.Id, TravelBookingStatuses.CheckedIn, null), ct);
        dto = (await issuer.DtosAsync(me, [await db.TravelBookings.AsNoTracking().FirstAsync(x => x.Id == b.Id, ct)], ct))[0];
        return done == 1
            ? new CheckInResultDto(CheckInOutcomes.CheckedIn, dto, $"{b.LeadGuestName} is checked in.")
            : new CheckInResultDto(CheckInOutcomes.AlreadyCheckedIn, dto, "Already checked in.");
    }

    static bool Verify(string publicKeySpki, byte[] payload, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeySpki), out _);
            return key.VerifyData(payload, Convert.FromBase64String(signature), HashAlgorithmName.SHA256);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }
}
