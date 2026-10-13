using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Travel;

/// <summary>
/// What's on Lets Travel and how to find it (FR-1, FR-2.1, FR-3.1, FR-3.2), and the vendor portal
/// where lodges and tour operators list it. Bookings are <see cref="TravelBookings"/>.
/// </summary>
public sealed class TravelCatalog(ChatDbContext db, TravelPhotos photos, IOptions<TravelOptions> options, TimeProvider clock)
{
    public const string Draft = "draft", Published = "published", Suspended = "suspended", Active = "active";

    DateTimeOffset Now => clock.GetUtcNow();
    DateOnly Today => DateOnly.FromDateTime(Now.UtcDateTime);

    public static bool IsAdmin(User user) =>
        user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(FinanceContract.AdminRole, StringComparer.OrdinalIgnoreCase);

    public async Task<TravelMeDto> MeAsync(User user, CancellationToken ct)
    {
        var vendor = await db.TravelVendors.AsNoTracking().FirstOrDefaultAsync(v => v.UserId == user.Id, ct);
        var kyc = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => p.KycStatus).FirstOrDefaultAsync(ct);
        return new TravelMeDto(vendor is null ? null : ToDto(vendor), vendor is null && kyc == BankingProfile.Approved, IsAdmin(user),
            "USD", options.Value.DisplayRates);
    }

    // ------------------------------------------------------------------ search (FR-1)

    /// <summary>
    /// Stays or tours matching a place, dates and party, with facets for the region's results
    /// (FR-1.2): the counts come from everything that matches the place, dates and party, before
    /// the faceted filters, so picking one type doesn't hide the others.
    /// </summary>
    public async Task<TravelSearchResultDto> SearchAsync(TravelSearchRequest r, CancellationToken ct)
    {
        if (r.Type == TravelItemTypes.Tour)
            return await SearchToursAsync(r, ct);
        if (r.Type != TravelItemTypes.Stay)
            throw new ChatRejectedException("Search stays or tours.");
        var (checkIn, checkOut) = ValidateDates(r.CheckIn, r.CheckOut, required: false);
        var rooms = Math.Clamp(r.Rooms, 1, TravelContract.MaxRoomsPerBooking);
        var guests = Math.Clamp(r.Guests, 1, 60);

        var query = db.TravelProperties.AsNoTracking().Where(p => p.Status == Published)
            .Where(p => db.TravelVendors.Any(v => v.UserId == p.VendorId && v.Status == Active));
        if (r.Query?.Trim() is { Length: > 0 } q)
            query = query.Where(p => p.City.Contains(q) || p.Country.Contains(q) || p.Name.Contains(q) || (p.Address != null && p.Address.Contains(q)));
        var properties = await query.Take(500).ToListAsync(ct);
        var ids = properties.Select(p => p.Id).ToList();
        var roomTypes = (await db.RoomTypes.AsNoTracking().Where(t => ids.Contains(t.PropertyId) && !t.Archived).ToListAsync(ct)).ToLookup(t => t.PropertyId);
        var nights = checkIn is null ? null : await NightsAsync([.. roomTypes.SelectMany(g => g).Select(t => t.Id)], checkIn.Value, checkOut!.Value, ct);
        var vendors = await VendorNamesAsync(properties.Select(p => p.VendorId), ct);

        var candidates = new List<(TravelProperty Property, TravelResultDto Result, IReadOnlyList<RoomType> Fits)>();
        foreach (var p in properties)
        {
            var distance = Distance(r, p.Lat, p.Lng);
            if (r.RadiusKm is { } radius && (distance is null || distance > radius))
                continue;
            // A room type fits the party: enough of it free on every night, holding everyone between the rooms.
            var fits = roomTypes[p.Id].Where(t => t.Capacity * rooms >= guests).ToList();
            decimal? from = null;
            var available = false;
            foreach (var t in fits)
            {
                decimal perNight;
                if (nights is not null)
                {
                    var stay = Stay(t, checkIn!.Value, checkOut!.Value, nights);
                    if (stay.Available < rooms)
                        continue;
                    available = true;
                    perNight = stay.Prices.Average();
                }
                else
                {
                    available = true;
                    perNight = t.NightlyPrice;
                }
                from = from is null ? perNight : Math.Min(from.Value, perNight);
            }
            if (fits.Count == 0)
                continue;
            candidates.Add((p, new TravelResultDto(p.Id, TravelItemTypes.Stay, p.Name, p.Type, p.City, p.Country, p.Lat, p.Lng, distance, p.Stars,
                photos.Cover(p.PhotoIds), from is null ? null : decimal.Round(from.Value, 2), "night", available,
                Summary(p.Description), vendors.GetValueOrDefault(p.VendorId, "")), fits));
        }

        var facets = new List<TravelFacetDto>();
        Count(facets, "property_type", candidates.Select(c => c.Property.Type), TravelContract.PropertyTypes);
        Count(facets, "amenity", candidates.SelectMany(c => Amenities(c.Property)), TravelContract.Amenities);
        Count(facets, "board_plan", candidates.SelectMany(c => c.Fits.Select(t => t.BoardPlan).Distinct()), TravelContract.BoardPlans);
        Count(facets, "cancellation", candidates.Select(c => c.Property.Cancellation), [.. TravelContract.CancellationPolicies.Select(x => (x.Code, x.Label))]);
        foreach (var stars in candidates.Where(c => c.Property.Stars is not null).GroupBy(c => c.Property.Stars!.Value).OrderByDescending(g => g.Key))
            facets.Add(new TravelFacetDto("stars", stars.Key.ToString(), $"{stars.Key}★", stars.Count()));

        var results = candidates
            .Where(c => r.PropertyTypes is not { Count: > 0 } types || types.Contains(c.Property.Type))
            .Where(c => r.MinStars is null || c.Property.Stars >= r.MinStars)
            .Where(c => r.Amenities is not { Count: > 0 } wanted || wanted.All(a => c.Property.Amenities.Contains($",{a},")))
            .Where(c => r.BoardPlans is not { Count: > 0 } plans || c.Fits.Any(t => plans.Contains(t.BoardPlan)))
            .Where(c => r.Cancellation is not { Count: > 0 } policies || policies.Contains(c.Property.Cancellation))
            .Where(c => r.MaxPrice is null || c.Result.FromPrice <= r.MaxPrice)
            .Select(c => c.Result);
        return new TravelSearchResultDto([.. Sort(results, r.Sort).Take(100)], facets, Now);
    }

    async Task<TravelSearchResultDto> SearchToursAsync(TravelSearchRequest r, CancellationToken ct)
    {
        var guests = Math.Clamp(r.Guests, 1, TravelContract.MaxGuestsPerTour);
        var from = r.CheckIn is { } a ? new DateTimeOffset(a.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : Now;
        var to = r.CheckOut is { } b ? new DateTimeOffset(b.ToDateTime(TimeOnly.MaxValue), TimeSpan.Zero) : Now.AddDays(TravelContract.MaxDaysAhead);
        if (from < Now)
            from = Now;

        var query = db.TourPackages.AsNoTracking().Where(t => t.Status == Published)
            .Where(t => db.TravelVendors.Any(v => v.UserId == t.VendorId && v.Status == Active));
        if (r.Query?.Trim() is { Length: > 0 } q)
            query = query.Where(t => t.City.Contains(q) || t.Country.Contains(q) || t.Title.Contains(q));
        var tours = await query.Take(500).ToListAsync(ct);
        var ids = tours.Select(t => t.Id).ToList();
        var departures = (await db.TourDepartures.AsNoTracking()
            .Where(d => ids.Contains(d.TourId) && d.Status != DepartureStatuses.Canceled && d.StartsAt >= from && d.StartsAt <= to)
            .ToListAsync(ct)).ToLookup(d => d.TourId);
        var vendors = await VendorNamesAsync(tours.Select(t => t.VendorId), ct);

        var candidates = new List<(TourPackage Tour, TravelResultDto Result)>();
        foreach (var t in tours)
        {
            var distance = Distance(r, t.Lat, t.Lng);
            if (r.RadiusKm is { } radius && (distance is null || distance > radius))
                continue;
            var open = departures[t.Id].Any(d => d.Seats - d.Held - d.Booked >= guests);
            candidates.Add((t, new TravelResultDto(t.Id, TravelItemTypes.Tour, t.Title, t.Kind, t.City, t.Country, t.Lat, t.Lng, distance, null,
                photos.Cover(t.PhotoIds), t.PricePerPerson, "person", open, Summary(t.Description), vendors.GetValueOrDefault(t.VendorId, ""))));
        }

        var facets = new List<TravelFacetDto>();
        Count(facets, "tour_kind", candidates.Select(c => c.Tour.Kind), TravelContract.TourKinds);
        Count(facets, "difficulty", candidates.Select(c => c.Tour.Difficulty), TravelContract.Difficulties);
        var results = candidates
            .Where(c => r.TourKinds is not { Count: > 0 } kinds || kinds.Contains(c.Tour.Kind))
            .Where(c => r.Difficulties is not { Count: > 0 } levels || levels.Contains(c.Tour.Difficulty))
            .Where(c => r.MaxPrice is null || c.Tour.PricePerPerson <= r.MaxPrice)
            .Select(c => c.Result);
        return new TravelSearchResultDto([.. Sort(results, r.Sort).Take(100)], facets, Now);
    }

    static IEnumerable<TravelResultDto> Sort(IEnumerable<TravelResultDto> results, string sort) => sort switch
    {
        "price_asc" => results.OrderBy(x => x.FromPrice ?? decimal.MaxValue),
        "price_desc" => results.OrderByDescending(x => x.FromPrice ?? 0),
        "distance" => results.OrderBy(x => x.DistanceKm ?? double.MaxValue),
        _ => results.OrderByDescending(x => x.Available).ThenByDescending(x => x.Stars ?? 0).ThenBy(x => x.FromPrice ?? decimal.MaxValue)
    };

    static void Count(List<TravelFacetDto> facets, string field, IEnumerable<string> values, IReadOnlyList<(string Code, string Label)> labels)
    {
        foreach (var g in values.GroupBy(v => v).OrderByDescending(g => g.Count()))
            facets.Add(new TravelFacetDto(field, g.Key, TravelContract.Label(labels, g.Key), g.Count()));
    }

    static double? Distance(TravelSearchRequest r, double? lat, double? lng)
    {
        if (r.Lat is not { } aLat || r.Lng is not { } aLng || lat is not { } bLat || lng is not { } bLng)
            return null;
        const double R = 6371;
        double Rad(double d) => d * Math.PI / 180;
        var h = Math.Pow(Math.Sin(Rad(bLat - aLat) / 2), 2) + Math.Cos(Rad(aLat)) * Math.Cos(Rad(bLat)) * Math.Pow(Math.Sin(Rad(bLng - aLng) / 2), 2);
        return Math.Round(2 * R * Math.Asin(Math.Min(1, Math.Sqrt(h))), 1);
    }

    static string? Summary(string? text) => text is null ? null : text.Length <= 180 ? text : text[..177].TrimEnd() + "…";

    static IEnumerable<string> Amenities(TravelProperty p) => p.Amenities.Split(',', StringSplitOptions.RemoveEmptyEntries);

    async Task<Dictionary<Guid, string>> VendorNamesAsync(IEnumerable<Guid> vendorIds, CancellationToken ct)
    {
        var ids = vendorIds.Distinct().ToList();
        return await db.TravelVendors.AsNoTracking().Where(v => ids.Contains(v.UserId)).ToDictionaryAsync(v => v.UserId, v => v.Name, ct);
    }

    // ------------------------------------------------------------------ stays and availability

    /// <summary>Check-in and check-out as asked, checked: from today, at most a year and a half ahead, 1 to 30 nights.</summary>
    public (DateOnly? CheckIn, DateOnly? CheckOut) ValidateDates(DateOnly? checkIn, DateOnly? checkOut, bool required)
    {
        if (checkIn is null && checkOut is null && !required)
            return (null, null);
        if (checkIn is not { } a || checkOut is not { } b)
            throw new ChatRejectedException("Choose check-in and check-out dates.");
        if (a < Today)
            throw new ChatRejectedException("Check-in can't be in the past.");
        if (a > Today.AddDays(TravelContract.MaxDaysAhead))
            throw new ChatRejectedException("That's too far ahead to book.");
        var nights = b.DayNumber - a.DayNumber;
        if (nights < 1 || nights > TravelContract.MaxNights)
            throw new ChatRejectedException($"A stay is 1 to {TravelContract.MaxNights} nights.");
        return (a, b);
    }

    public async Task<Dictionary<(Guid, DateOnly), RoomNight>> NightsAsync(IReadOnlyList<Guid> roomTypeIds, DateOnly from, DateOnly to, CancellationToken ct) =>
        await db.RoomNights.AsNoTracking().Where(n => roomTypeIds.Contains(n.RoomTypeId) && n.Date >= from && n.Date < to)
            .ToDictionaryAsync(n => (n.RoomTypeId, n.Date), ct);

    /// <summary>Rooms free on every night of a stay (0 if any night is closed), and each night's price.</summary>
    public static (int Available, IReadOnlyList<decimal> Prices) Stay(RoomType t, DateOnly from, DateOnly to, Dictionary<(Guid, DateOnly), RoomNight> nights)
    {
        var available = t.Units;
        var prices = new List<decimal>();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            if (nights.TryGetValue((t.Id, d), out var n))
            {
                available = n.Closed ? 0 : Math.Min(available, t.Units - n.Held - n.Booked);
                prices.Add(n.Price ?? t.NightlyPrice);
            }
            else
                prices.Add(t.NightlyPrice);
        }
        return (Math.Max(0, available), prices);
    }

    public async Task<PropertyDto?> PropertyAsync(User user, Guid id, DateOnly? checkIn, DateOnly? checkOut, CancellationToken ct)
    {
        var p = await db.TravelProperties.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct);
        if (p is null || p.Status != Published && p.VendorId != user.Id && !IsAdmin(user))
            return null;
        var (a, b) = ValidateDates(checkIn, checkOut, required: false);
        var rooms = await db.RoomTypes.AsNoTracking().Where(t => t.PropertyId == id && !t.Archived).OrderBy(t => t.NightlyPrice).ToListAsync(ct);
        var nights = a is null ? null : await NightsAsync([.. rooms.Select(t => t.Id)], a.Value, b!.Value, ct);
        var vendor = await db.TravelVendors.AsNoTracking().FirstAsync(v => v.UserId == p.VendorId, ct);
        return new PropertyDto(p.Id, p.VendorId, vendor.Name, p.Name, p.Type, p.Description, p.City, p.Country, p.Address, p.Lat, p.Lng, p.Stars,
            [.. Amenities(p)], p.Cancellation, p.CheckInTime, p.CheckOutTime, p.Status, photos.ToDtos(p.PhotoIds),
            [.. rooms.Select(t =>
            {
                if (nights is null)
                    return new RoomTypeDto(t.Id, t.Name, t.Kind, t.BoardPlan, t.Capacity, t.Units, t.NightlyPrice, t.Description, null, null, null);
                var stay = Stay(t, a!.Value, b!.Value, nights);
                return new RoomTypeDto(t.Id, t.Name, t.Kind, t.BoardPlan, t.Capacity, t.Units, t.NightlyPrice, t.Description, stay.Available, stay.Prices.Sum(), stay.Prices);
            })],
            p.VendorId == user.Id);
    }

    public async Task<TourDto?> TourAsync(User user, Guid id, CancellationToken ct)
    {
        var t = await db.TourPackages.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        if (t is null || t.Status != Published && t.VendorId != user.Id && !IsAdmin(user))
            return null;
        var mine = t.VendorId == user.Id;
        var vendor = await db.TravelVendors.AsNoTracking().FirstAsync(v => v.UserId == t.VendorId, ct);
        var addOns = await db.TourAddOns.AsNoTracking().Where(a => a.TourId == id && !a.Archived).OrderByDescending(a => a.Mandatory).ThenBy(a => a.Name).ToListAsync(ct);
        // Travellers see what's ahead; the vendor also sees the last month.
        var since = mine ? Now.AddDays(-30) : Now;
        var departures = await db.TourDepartures.AsNoTracking().Where(d => d.TourId == id && d.StartsAt >= since && (mine || d.Status != DepartureStatuses.Canceled))
            .OrderBy(d => d.StartsAt).Take(200).ToListAsync(ct);
        return new TourDto(t.Id, t.VendorId, vendor.Name, t.Title, t.Kind, t.Description, t.City, t.Country, t.Lat, t.Lng, t.DurationHours, t.MaxCapacity,
            t.Difficulty, t.PricePerPerson, t.WeatherDependent, t.Includes, t.MeetingPoint, t.Status, photos.ToDtos(t.PhotoIds),
            [.. addOns.Select(a => new TourAddOnDto(a.Id, a.Name, a.Price, a.PerPerson, a.Mandatory))],
            [.. departures.Select(ToDto)], mine);
    }

    public static DepartureDto ToDto(TourDeparture d) =>
        new(d.Id, d.StartsAt, d.Seats, Math.Max(0, d.Seats - d.Held - d.Booked), d.Guide, d.Status, d.Note);

    // ------------------------------------------------------------------ vendors

    public async Task<TravelVendorDto> SaveVendorAsync(User user, SaveVendorRequest r, CancellationToken ct)
    {
        var vendor = await db.TravelVendors.FirstOrDefaultAsync(v => v.UserId == user.Id, ct);
        if (vendor is null)
        {
            var kyc = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == user.Id).Select(p => p.KycStatus).FirstOrDefaultAsync(ct);
            if (kyc != BankingProfile.Approved)
                throw new ChatRejectedException("Verify your identity under Me > Wallet before selling stays or tours.");
            vendor = new TravelVendor { UserId = user.Id, Name = "", CreatedAt = Now };
            db.TravelVendors.Add(vendor);
        }
        vendor.Name = Required(r.Name, "your business name", TravelContract.MaxNameLength);
        vendor.Description = Optional(r.Description, 1000);
        vendor.Phone = Optional(r.Phone, 40);
        vendor.Email = Optional(r.Email, 200);
        await db.SaveChangesAsync(ct);
        return ToDto(vendor);
    }

    public async Task<TravelVendor> VendorAsync(User user, CancellationToken ct)
    {
        var vendor = await db.TravelVendors.AsNoTracking().FirstOrDefaultAsync(v => v.UserId == user.Id, ct)
            ?? throw new ChatRejectedException("Set up your vendor profile first.");
        if (vendor.Status != Active)
            throw new ChatRejectedException("Your vendor account is suspended. Contact Ndeipi support.");
        return vendor;
    }

    TravelVendorDto ToDto(TravelVendor v) => new(v.UserId, v.Name, v.Description, v.Phone, v.Email, v.Status, (int)options.Value.FeePercent);

    public async Task<IReadOnlyList<VendorListingDto>> ListingsAsync(Guid vendorId, CancellationToken ct)
    {
        var properties = await db.TravelProperties.AsNoTracking().Where(p => p.VendorId == vendorId).OrderBy(p => p.Name).ToListAsync(ct);
        var tours = await db.TourPackages.AsNoTracking().Where(t => t.VendorId == vendorId).OrderBy(t => t.Title).ToListAsync(ct);
        var today = Today;
        var upcoming = await db.TravelBookingItems.AsNoTracking()
            .Where(i => db.TravelBookings.Any(b => b.Id == i.BookingId && b.VendorId == vendorId && b.Status == TravelBookingStatuses.Confirmed))
            .Where(i => (i.CheckOut != null && i.CheckOut >= today) || (i.StartsAt != null && i.StartsAt >= Now))
            .GroupBy(i => i.ListingId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return
        [
            .. properties.Select(p => new VendorListingDto(p.Id, TravelItemTypes.Stay, p.Name, p.Type, p.City, p.Status, photos.Cover(p.PhotoIds), upcoming.GetValueOrDefault(p.Id))),
            .. tours.Select(t => new VendorListingDto(t.Id, TravelItemTypes.Tour, t.Title, t.Kind, t.City, t.Status, photos.Cover(t.PhotoIds), upcoming.GetValueOrDefault(t.Id)))
        ];
    }

    // ------------------------------------------------------------------ properties and rooms (FR-2.1)

    public async Task<PropertyDto> SavePropertyAsync(User user, Guid? id, SavePropertyRequest r, CancellationToken ct)
    {
        var vendor = await VendorAsync(user, ct);
        TravelProperty p;
        if (id is null)
        {
            p = new TravelProperty { Id = Guid.NewGuid(), VendorId = vendor.UserId, Name = "", Type = "", City = "", Country = "", Cancellation = "", CreatedAt = Now };
            db.TravelProperties.Add(p);
        }
        else
            p = await OwnPropertyAsync(user, id.Value, ct);

        p.Name = Required(r.Name, "the property's name", TravelContract.MaxNameLength);
        p.Type = OneOf(r.Type, TravelContract.PropertyTypes, "property type");
        p.Description = Optional(r.Description, TravelContract.MaxDescriptionLength);
        p.City = Required(r.City, "the town or area", 80);
        p.Country = Required(r.Country, "the country", 80);
        p.Address = Optional(r.Address, 300);
        (p.Lat, p.Lng) = Coordinates(r.Lat, r.Lng);
        p.Stars = r.Stars is null or 0 ? null : r.Stars is >= 1 and <= 5 ? r.Stars : throw new ChatRejectedException("Stars are 1 to 5.");
        var amenities = (r.Amenities ?? []).Distinct().ToList();
        if (amenities.FirstOrDefault(a => TravelContract.Amenities.All(x => x.Code != a)) is { } unknown)
            throw new ChatRejectedException($"\"{unknown}\" isn't an amenity Lets Travel knows.");
        p.Amenities = "," + string.Join(',', amenities) + ",";
        p.Cancellation = TravelContract.CancellationPolicies.Any(c => c.Code == r.Cancellation) ? r.Cancellation : throw new ChatRejectedException("Choose a cancellation policy.");
        p.CheckInTime = Time(r.CheckInTime, "check-in");
        p.CheckOutTime = Time(r.CheckOutTime, "check-out");
        p.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return (await PropertyAsync(user, p.Id, null, null, ct))!;
    }

    public async Task<RoomTypeDto> SaveRoomTypeAsync(User user, Guid propertyId, Guid? roomTypeId, SaveRoomTypeRequest r, CancellationToken ct)
    {
        await OwnPropertyAsync(user, propertyId, ct);
        RoomType t;
        if (roomTypeId is null)
        {
            t = new RoomType { Id = Guid.NewGuid(), PropertyId = propertyId, Name = "", Kind = "", BoardPlan = "" };
            db.RoomTypes.Add(t);
        }
        else
            t = await db.RoomTypes.FirstOrDefaultAsync(x => x.Id == roomTypeId && x.PropertyId == propertyId && !x.Archived, ct)
                ?? throw new ChatRejectedException("That room type isn't here any more.");

        t.Name = Required(r.Name, "the room's name", 80);
        t.Kind = OneOf(r.Kind, TravelContract.RoomKinds, "room kind");
        t.BoardPlan = OneOf(r.BoardPlan, TravelContract.BoardPlans, "board plan");
        t.Capacity = r.Capacity is >= 1 and <= 20 ? r.Capacity : throw new ChatRejectedException("A room sleeps 1 to 20.");
        t.Units = r.Units is >= 1 and <= 500 ? r.Units : throw new ChatRejectedException("You have 1 to 500 of this room.");
        t.NightlyPrice = Price(r.NightlyPrice);
        t.Description = Optional(r.Description, 1000);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ChatRejectedException("Someone changed this room at the same moment. Reload and try again.");
        }
        return new RoomTypeDto(t.Id, t.Name, t.Kind, t.BoardPlan, t.Capacity, t.Units, t.NightlyPrice, t.Description, null, null, null);
    }

    public async Task ArchiveRoomTypeAsync(User user, Guid propertyId, Guid roomTypeId, CancellationToken ct)
    {
        await OwnPropertyAsync(user, propertyId, ct);
        await db.RoomTypes.Where(t => t.Id == roomTypeId && t.PropertyId == propertyId).ExecuteUpdateAsync(s => s.SetProperty(t => t.Archived, true), ct);
    }

    /// <summary>The rate calendar: each night's price, rooms, bookings and holds.</summary>
    public async Task<IReadOnlyList<RoomNightDto>> CalendarAsync(User user, Guid propertyId, Guid roomTypeId, DateOnly from, int days, CancellationToken ct)
    {
        await OwnPropertyAsync(user, propertyId, ct);
        var t = await db.RoomTypes.AsNoTracking().FirstOrDefaultAsync(x => x.Id == roomTypeId && x.PropertyId == propertyId, ct)
            ?? throw new ChatRejectedException("That room type isn't here any more.");
        var to = from.AddDays(Math.Clamp(days, 1, 92));
        var nights = await NightsAsync([t.Id], from, to, ct);
        var list = new List<RoomNightDto>();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            var n = nights.GetValueOrDefault((t.Id, d));
            list.Add(new RoomNightDto(d, n?.Price ?? t.NightlyPrice, t.Units, n?.Booked ?? 0, n?.Held ?? 0, n?.Closed ?? false));
        }
        return list;
    }

    /// <summary>Sets a price for, or stops (or restarts) selling, a range of nights.</summary>
    public async Task SetRatesAsync(User user, Guid propertyId, Guid roomTypeId, SetRatesRequest r, CancellationToken ct)
    {
        await OwnPropertyAsync(user, propertyId, ct);
        if (!await db.RoomTypes.AnyAsync(x => x.Id == roomTypeId && x.PropertyId == propertyId, ct))
            throw new ChatRejectedException("That room type isn't here any more.");
        if (r.To < r.From || r.To.DayNumber - r.From.DayNumber > 366)
            throw new ChatRejectedException("Choose up to a year of nights.");
        var price = r.Price is null ? (decimal?)null : Price(r.Price.Value);
        for (var d = r.From; d <= r.To; d = d.AddDays(1))
        {
            await EnsureNightAsync(roomTypeId, d, ct);
            var date = d;
            await db.RoomNights.Where(n => n.RoomTypeId == roomTypeId && n.Date == date).ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Price, n => price ?? n.Price)
                .SetProperty(n => n.Closed, n => r.Closed ?? n.Closed), ct);
        }
    }

    /// <summary>Makes sure a night has a counter row. Two requests may race; the loser finds the winner's row.</summary>
    public async Task EnsureNightAsync(Guid roomTypeId, DateOnly date, CancellationToken ct)
    {
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [RoomNights] ([RoomTypeId], [Date], [Held], [Booked], [Closed])
                SELECT {roomTypeId}, {date}, 0, 0, 0
                WHERE NOT EXISTS (SELECT 1 FROM [RoomNights] WITH (UPDLOCK, HOLDLOCK) WHERE [RoomTypeId] = {roomTypeId} AND [Date] = {date})
                """, ct);
        }
        catch (Microsoft.Data.SqlClient.SqlException e) when (e.Number is 2627 or 2601 or 1205)
        {
            // Already there.
        }
    }

    // ------------------------------------------------------------------ tours (FR-3)

    public async Task<TourDto> SaveTourAsync(User user, Guid? id, SaveTourRequest r, CancellationToken ct)
    {
        var vendor = await VendorAsync(user, ct);
        TourPackage t;
        if (id is null)
        {
            t = new TourPackage { Id = Guid.NewGuid(), VendorId = vendor.UserId, Title = "", Kind = "", City = "", Country = "", Difficulty = "", CreatedAt = Now };
            db.TourPackages.Add(t);
        }
        else
            t = await OwnTourAsync(user, id.Value, ct);

        t.Title = Required(r.Title, "the tour's name", TravelContract.MaxNameLength);
        t.Kind = OneOf(r.Kind, TravelContract.TourKinds, "kind of tour");
        t.Description = Optional(r.Description, TravelContract.MaxDescriptionLength);
        t.City = Required(r.City, "the town or area", 80);
        t.Country = Required(r.Country, "the country", 80);
        (t.Lat, t.Lng) = Coordinates(r.Lat, r.Lng);
        t.DurationHours = r.DurationHours is > 0 and <= 24 * 21 ? Math.Round(r.DurationHours, 1) : throw new ChatRejectedException("A tour lasts up to 21 days.");
        t.MaxCapacity = r.MaxCapacity is >= 1 and <= 200 ? r.MaxCapacity : throw new ChatRejectedException("A departure takes 1 to 200 people.");
        t.Difficulty = OneOf(r.Difficulty, TravelContract.Difficulties, "difficulty");
        t.PricePerPerson = Price(r.PricePerPerson);
        t.WeatherDependent = r.WeatherDependent;
        t.Includes = Optional(r.Includes, 1000);
        t.MeetingPoint = Optional(r.MeetingPoint, 300);
        t.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return (await TourAsync(user, t.Id, ct))!;
    }

    public async Task<TourAddOnDto> SaveAddOnAsync(User user, Guid tourId, Guid? addOnId, SaveAddOnRequest r, CancellationToken ct)
    {
        await OwnTourAsync(user, tourId, ct);
        TourAddOn a;
        if (addOnId is null)
        {
            a = new TourAddOn { Id = Guid.NewGuid(), TourId = tourId, Name = "" };
            db.TourAddOns.Add(a);
        }
        else
            a = await db.TourAddOns.FirstOrDefaultAsync(x => x.Id == addOnId && x.TourId == tourId && !x.Archived, ct)
                ?? throw new ChatRejectedException("That extra isn't here any more.");
        a.Name = Required(r.Name, "the extra's name", 80);
        a.Price = r.Price is >= 0 and <= 100_000 ? decimal.Round(r.Price, 2) : throw new ChatRejectedException("Prices are 0 to 100,000 dollars.");
        (a.PerPerson, a.Mandatory) = (r.PerPerson, r.Mandatory);
        await db.SaveChangesAsync(ct);
        return new TourAddOnDto(a.Id, a.Name, a.Price, a.PerPerson, a.Mandatory);
    }

    public async Task ArchiveAddOnAsync(User user, Guid tourId, Guid addOnId, CancellationToken ct)
    {
        await OwnTourAsync(user, tourId, ct);
        await db.TourAddOns.Where(a => a.Id == addOnId && a.TourId == tourId).ExecuteUpdateAsync(s => s.SetProperty(a => a.Archived, true), ct);
    }

    public async Task<DepartureDto> AddDepartureAsync(User user, Guid tourId, SaveDepartureRequest r, CancellationToken ct)
    {
        var t = await OwnTourAsync(user, tourId, ct);
        if (r.StartsAt <= Now || r.StartsAt > Now.AddDays(TravelContract.MaxDaysAhead))
            throw new ChatRejectedException("Choose a departure time ahead, within a year and a half.");
        var d = new TourDeparture
        {
            Id = Guid.NewGuid(),
            TourId = tourId,
            StartsAt = r.StartsAt,
            Seats = r.Seats is null ? t.MaxCapacity : r.Seats is >= 1 and <= 200 ? r.Seats.Value : throw new ChatRejectedException("A departure takes 1 to 200 people."),
            Guide = Optional(r.Guide, 120),
            Status = DepartureStatuses.Scheduled
        };
        db.TourDepartures.Add(d);
        await db.SaveChangesAsync(ct);
        return ToDto(d);
    }

    // ------------------------------------------------------------------ photos and publishing

    public async Task<IReadOnlyList<TravelPhotoDto>> AddPhotoAsync(User user, string type, Guid id, IFormFile file, CancellationToken ct)
    {
        var listing = await ListingPhotosAsync(user, type, id, ct);
        if (TravelPhotos.Ids(listing.Get()).Count >= TravelContract.MaxPhotos)
            throw new ChatRejectedException($"Up to {TravelContract.MaxPhotos} photos.");
        var photo = await photos.UploadAsync(user.Id, file, ct);
        listing.Set(string.Join(',', TravelPhotos.Ids(listing.Get()).Append(photo.Id)));
        await db.SaveChangesAsync(ct);
        return photos.ToDtos(listing.Get());
    }

    public async Task<IReadOnlyList<TravelPhotoDto>> RemovePhotoAsync(User user, string type, Guid id, Guid photoId, CancellationToken ct)
    {
        var listing = await ListingPhotosAsync(user, type, id, ct);
        listing.Set(string.Join(',', TravelPhotos.Ids(listing.Get()).Where(p => p != photoId)));
        await db.SaveChangesAsync(ct);
        return photos.ToDtos(listing.Get());
    }

    async Task<(Func<string> Get, Action<string> Set)> ListingPhotosAsync(User user, string type, Guid id, CancellationToken ct)
    {
        if (type == TravelItemTypes.Stay)
        {
            var p = await OwnPropertyAsync(user, id, ct);
            return (() => p.PhotoIds, v => (p.PhotoIds, p.UpdatedAt) = (v, Now));
        }
        var t = await OwnTourAsync(user, id, ct);
        return (() => t.PhotoIds, v => (t.PhotoIds, t.UpdatedAt) = (v, Now));
    }

    /// <summary>Publishes or withdraws a listing. A stay needs a room and a tour a price before it can be booked.</summary>
    public async Task PublishAsync(User user, string type, Guid id, bool published, CancellationToken ct)
    {
        if (type == TravelItemTypes.Stay)
        {
            var p = await OwnPropertyAsync(user, id, ct);
            if (p.Status == Suspended)
                throw new ChatRejectedException("Ndeipi suspended this listing. Contact support.");
            if (published && !await db.RoomTypes.AnyAsync(t => t.PropertyId == id && !t.Archived, ct))
                throw new ChatRejectedException("Add at least one room type before publishing.");
            p.Status = published ? Published : Draft;
        }
        else
        {
            var t = await OwnTourAsync(user, id, ct);
            if (t.Status == Suspended)
                throw new ChatRejectedException("Ndeipi suspended this listing. Contact support.");
            t.Status = published ? Published : Draft;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>NFR-6: admins suspend a vendor or one listing (and restore it).</summary>
    public async Task AdminStatusAsync(User user, string type, Guid id, SetStatusRequest r, CancellationToken ct)
    {
        if (!IsAdmin(user))
            throw new ChatRejectedException("Only Ndeipi admins can do that.");
        var suspend = r.Status == Suspended;
        if (r.Status is not (Suspended or Active))
            throw new ChatRejectedException("Suspend or restore.");
        var updated = type switch
        {
            "vendor" => await db.TravelVendors.Where(v => v.UserId == id).ExecuteUpdateAsync(s => s.SetProperty(v => v.Status, suspend ? Suspended : Active), ct),
            TravelItemTypes.Stay => await db.TravelProperties.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, suspend ? Suspended : Draft), ct),
            TravelItemTypes.Tour => await db.TourPackages.Where(t => t.Id == id).ExecuteUpdateAsync(s => s.SetProperty(t => t.Status, suspend ? Suspended : Draft), ct),
            _ => 0
        };
        if (updated == 0)
            throw new ChatRejectedException("Nothing to change there.");
    }

    public async Task<TravelProperty> OwnPropertyAsync(User user, Guid id, CancellationToken ct) =>
        await db.TravelProperties.FirstOrDefaultAsync(p => p.Id == id && p.VendorId == user.Id, ct) ?? throw new ChatRejectedException("That property isn't yours.");

    public async Task<TourPackage> OwnTourAsync(User user, Guid id, CancellationToken ct) =>
        await db.TourPackages.FirstOrDefaultAsync(t => t.Id == id && t.VendorId == user.Id, ct) ?? throw new ChatRejectedException("That tour isn't yours.");

    // ------------------------------------------------------------------ validation

    static string Required(string? value, string what, int max) =>
        value?.Trim() is { Length: > 0 } v ? v.Length <= max ? v : throw new ChatRejectedException($"Keep {what} under {max} characters.") : throw new ChatRejectedException($"Enter {what}.");

    static string? Optional(string? value, int max) =>
        value?.Trim() is { Length: > 0 } v ? v.Length <= max ? v : throw new ChatRejectedException($"Keep it under {max} characters.") : null;

    static string OneOf(string? value, IReadOnlyList<(string Code, string Label)> allowed, string what) =>
        allowed.Any(a => a.Code == value) ? value! : throw new ChatRejectedException($"Choose a {what}.");

    static decimal Price(decimal value) =>
        value is >= 1 and <= 100_000 ? decimal.Round(value, 2) : throw new ChatRejectedException("Prices are 1 to 100,000 dollars.");

    static (double?, double?) Coordinates(double? lat, double? lng) =>
        (lat, lng) switch
        {
            (null, null) => (null, null),
            ({ } a, { } b) when a is >= -90 and <= 90 && b is >= -180 and <= 180 => (Math.Round(a, 6), Math.Round(b, 6)),
            _ => throw new ChatRejectedException("That location isn't a valid latitude and longitude.")
        };

    static string Time(string? value, string what) =>
        TimeOnly.TryParseExact(value, "HH:mm", out var t) ? t.ToString("HH:mm") : throw new ChatRejectedException($"Enter the {what} time as HH:mm.");
}
