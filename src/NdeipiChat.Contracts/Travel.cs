namespace NdeipiChat.Contracts;

/// <summary>
/// Lets Travel (Hotel &amp; Tours Booking SRS 1.0.0): find and book stays and tours across Southern
/// Africa, pay from the Ndeipi wallet, and travel on an offline QR voucher. Vendors (lodges, hotels and
/// tour operators who have verified their identity) list properties, rooms, tours and departures in
/// their own portal.
/// </summary>
public static class TravelContract
{
    public const string AppId = "travel";
    public const string BasePath = "api/travel";

    /// <summary>FR-2.3: how long a room or seat is held while the traveller pays.</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(15);

    /// <summary>A stay may be booked up to this far ahead, and be this many nights long.</summary>
    public const int MaxDaysAhead = 540, MaxNights = 30;

    public const int MaxRoomsPerBooking = 10, MaxGuestsPerTour = 30, MaxPhotos = 8;
    public const int MaxNameLength = 120, MaxDescriptionLength = 4000, MaxRequestsLength = 500;

    /// <summary>A booking's offline voucher code changes every this many seconds, like event tickets.</summary>
    public const int QrWindowSeconds = 30;

    /// <summary>How many windows either side a check-in scanner accepts, for clocks that drift.</summary>
    public const int QrWindowTolerance = 4;

    /// <summary>Live updates for one user: their bookings, and as a vendor, their bookings received. "travel:{userId:N}".</summary>
    public static string Topic(Guid userId) => $"travel:{userId:N}";

    /// <summary>What the voucher's QR proves: this booking, in this time window, shown on the device the traveller registered.</summary>
    public static byte[] QrPayload(Guid bookingId, long window) => System.Text.Encoding.UTF8.GetBytes($"lt|{bookingId:N}|{window}");

    public static string QrText(Guid bookingId, long window, string signature) => $"LT1.{bookingId:N}.{window}.{signature}";

    public static bool TryParseQr(string text, out Guid bookingId, out long window, out string signature)
    {
        (bookingId, window, signature) = (Guid.Empty, 0, "");
        var parts = text.Trim().Split('.', 4);
        return parts is ["LT1", var id, var w, var s] && Guid.TryParseExact(id, "N", out bookingId) && long.TryParse(w, out window)
            && (signature = s).Length > 0;
    }

    public static string Money(decimal amount, string currency = "USD") =>
        currency is "USD" or "usd" or "usdc"
            ? "$" + amount.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture)
            : amount.ToString("#,0.00", System.Globalization.CultureInfo.InvariantCulture) + " " + currency.ToUpperInvariant();

    public static readonly IReadOnlyList<(string Code, string Label)> PropertyTypes =
    [
        ("hotel", "Hotel"), ("lodge", "Safari lodge"), ("eco_lodge", "Eco-lodge"), ("bush_camp", "Bush camp"), ("guesthouse", "Guesthouse"),
        ("chalet", "Chalets"), ("campsite", "Campsite"), ("apartment", "Apartment")
    ];

    public static readonly IReadOnlyList<(string Code, string Label)> RoomKinds =
        [("standard", "Standard"), ("deluxe", "Deluxe"), ("suite", "Suite"), ("family", "Family room"), ("chalet", "Chalet"), ("tent", "Safari tent"), ("campsite", "Campsite")];

    /// <summary>FR-2.1: what's included with the room.</summary>
    public static readonly IReadOnlyList<(string Code, string Label)> BoardPlans =
        [("room_only", "Room only"), ("bed_breakfast", "Bed & breakfast"), ("half_board", "Half board"), ("full_board", "Full board"), ("all_inclusive", "All-inclusive")];

    public static readonly IReadOnlyList<(string Code, string Label)> Amenities =
    [
        ("wifi", "Wi-Fi"), ("pool", "Pool"), ("restaurant", "Restaurant"), ("bar", "Bar"), ("parking", "Parking"), ("airport_transfer", "Airport transfer"),
        ("game_drives", "Game drives"), ("bush_walks", "Bush walks"), ("boat_trips", "Boat trips"), ("spa", "Spa"), ("family_friendly", "Family friendly"),
        ("wheelchair", "Wheelchair access"), ("solar_power", "Solar power"), ("pets", "Pets allowed")
    ];

    public static readonly IReadOnlyList<(string Code, string Label, string Detail)> CancellationPolicies =
    [
        ("flexible", "Flexible", "Free cancellation until 24 hours before arrival."),
        ("moderate", "Moderate", "Free cancellation until 5 days before arrival."),
        ("strict", "Strict", "Non-refundable within 30 days of arrival.")
    ];

    public static readonly IReadOnlyList<(string Code, string Label)> TourKinds =
    [
        ("game_drive", "Game drive"), ("walking_safari", "Walking safari"), ("river_cruise", "River cruise"), ("city_tour", "City tour"),
        ("cultural", "Cultural visit"), ("adventure", "Adventure"), ("scenic_flight", "Scenic flight"), ("multi_day_safari", "Multi-day safari")
    ];

    public static readonly IReadOnlyList<(string Code, string Label)> Difficulties = [("easy", "Easy"), ("moderate", "Moderate"), ("challenging", "Challenging")];

    public static string Label(IReadOnlyList<(string Code, string Label)> list, string code) =>
        list.FirstOrDefault(x => x.Code == code).Label ?? code;
}

public static class TravelItemTypes
{
    public const string Stay = "stay", Tour = "tour";
}

public static class TravelBookingStatuses
{
    /// <summary>Paid for from the wallet; waiting for the transfer to confirm.</summary>
    public const string PendingPayment = "pending_payment";
    public const string Confirmed = "confirmed";
    public const string CheckedIn = "checked_in";
    public const string Failed = "failed";
    /// <summary>Cancelled by the vendor (and refunded), or a departure that was called off.</summary>
    public const string Canceled = "canceled";
}

public static class DepartureStatuses
{
    public const string Scheduled = "scheduled", Rescheduled = "rescheduled", Canceled = "canceled";
}

// ---- Search (FR-1) ----

/// <summary>
/// One search. Stays need dates to show availability; without them every listed property shows its
/// lowest rate. Location is a free-text place (matched against city, country and name) or a point and radius.
/// </summary>
public sealed record TravelSearchRequest(
    string Type,
    string? Query = null,
    double? Lat = null,
    double? Lng = null,
    double? RadiusKm = null,
    DateOnly? CheckIn = null,
    DateOnly? CheckOut = null,
    int Guests = 2,
    int Rooms = 1,
    IReadOnlyList<string>? PropertyTypes = null,
    int? MinStars = null,
    IReadOnlyList<string>? Amenities = null,
    IReadOnlyList<string>? BoardPlans = null,
    IReadOnlyList<string>? Cancellation = null,
    IReadOnlyList<string>? TourKinds = null,
    IReadOnlyList<string>? Difficulties = null,
    decimal? MaxPrice = null,
    string Sort = "recommended");

public sealed record TravelFacetDto(string Field, string Code, string Label, int Count);

public sealed record TravelResultDto(
    Guid Id,
    string Type,
    string Name,
    string Kind,
    string City,
    string Country,
    double? Lat,
    double? Lng,
    double? DistanceKm,
    int? Stars,
    string? PhotoUrl,
    decimal? FromPrice,
    string PriceUnit,
    bool Available,
    string? Summary,
    string VendorName);

public sealed record TravelSearchResultDto(IReadOnlyList<TravelResultDto> Results, IReadOnlyList<TravelFacetDto> Facets, DateTimeOffset AsOf);

/// <summary>A place with published stays or tours, for the Explore page's top destinations.</summary>
public sealed record TravelDestinationDto(string City, string Country, int Stays, int Tours, string? PhotoUrl);

// ---- Stays (FR-2) ----

public sealed record TravelPhotoDto(Guid Id, string Url, string ThumbUrl);

public sealed record RoomTypeDto(
    Guid Id, string Name, string Kind, string BoardPlan, int Capacity, int Units, decimal NightlyPrice, string? Description,
    int? AvailableUnits, decimal? StayPrice, IReadOnlyList<decimal>? NightPrices);

public sealed record PropertyDto(
    Guid Id,
    Guid VendorId,
    string VendorName,
    string Name,
    string Type,
    string? Description,
    string City,
    string Country,
    string? Address,
    double? Lat,
    double? Lng,
    int? Stars,
    IReadOnlyList<string> Amenities,
    string Cancellation,
    string CheckInTime,
    string CheckOutTime,
    string Status,
    IReadOnlyList<TravelPhotoDto> Photos,
    IReadOnlyList<RoomTypeDto> Rooms,
    bool IsMine);

public sealed record SavePropertyRequest(
    string Name, string Type, string? Description, string City, string Country, string? Address, double? Lat, double? Lng, int? Stars,
    IReadOnlyList<string>? Amenities, string Cancellation, string CheckInTime, string CheckOutTime);

public sealed record SaveRoomTypeRequest(string Name, string Kind, string BoardPlan, int Capacity, int Units, decimal NightlyPrice, string? Description);

/// <summary>Vendor's rate calendar (FR-2.1): a price for some nights, or stop selling them.</summary>
public sealed record SetRatesRequest(DateOnly From, DateOnly To, decimal? Price, bool? Closed);

public sealed record RoomNightDto(DateOnly Date, decimal Price, int Units, int Booked, int Held, bool Closed);

// ---- Tours (FR-3) ----

public sealed record TourAddOnDto(Guid Id, string Name, decimal Price, bool PerPerson, bool Mandatory);

public sealed record DepartureDto(Guid Id, DateTimeOffset StartsAt, int Seats, int Available, string? Guide, string Status, string? Note);

public sealed record TourDto(
    Guid Id,
    Guid VendorId,
    string VendorName,
    string Title,
    string Kind,
    string? Description,
    string City,
    string Country,
    double? Lat,
    double? Lng,
    double DurationHours,
    int MaxCapacity,
    string Difficulty,
    decimal PricePerPerson,
    bool WeatherDependent,
    string? Includes,
    string? MeetingPoint,
    string Status,
    IReadOnlyList<TravelPhotoDto> Photos,
    IReadOnlyList<TourAddOnDto> AddOns,
    IReadOnlyList<DepartureDto> Departures,
    bool IsMine);

public sealed record SaveTourRequest(
    string Title, string Kind, string? Description, string City, string Country, double? Lat, double? Lng, double DurationHours,
    int MaxCapacity, string Difficulty, decimal PricePerPerson, bool WeatherDependent, string? Includes, string? MeetingPoint);

public sealed record SaveAddOnRequest(string Name, decimal Price, bool PerPerson, bool Mandatory);

public sealed record SaveDepartureRequest(DateTimeOffset StartsAt, int? Seats, string? Guide);

/// <summary>FR-3.3: move a departure (weather, a breakdown) or call it off; every booked traveller is told.</summary>
public sealed record RescheduleRequest(DateTimeOffset? StartsAt, bool Cancel, string Reason);

// ---- Checkout (FR-2.3, FR-4) ----

public sealed record TravelHoldRequest(
    string Type, Guid ItemId, DateOnly? CheckIn = null, DateOnly? CheckOut = null, int Quantity = 1, int Guests = 1,
    IReadOnlyList<Guid>? AddOnIds = null);

public sealed record TravelLineDto(string Label, decimal Amount);

public sealed record TravelHoldDto(
    Guid Id, string Type, Guid ItemId, string Title, string Subtitle, DateOnly? CheckIn, DateOnly? CheckOut, DateTimeOffset? StartsAt,
    int Quantity, int Guests, IReadOnlyList<TravelLineDto> Lines, decimal Total, string Currency, DateTimeOffset ExpiresAt, string Status,
    IReadOnlyList<TravelLineDto> Approximations, decimal MaxPointsDiscount, int PointsBalance, int PointsPerDollar);

/// <param name="RedeemPoints">FR-4.3: spend Believe Points for a discount, up to Ndeipi's fee on this booking.</param>
public sealed record TravelCheckoutRequest(string LeadGuestName, string? Phone, string? IdNumber, string? SpecialRequests, bool RedeemPoints = false);

public sealed record TravelBookingItemDto(
    string Type, Guid ReferenceId, string Title, string Subtitle, DateOnly? CheckIn, DateOnly? CheckOut, DateTimeOffset? StartsAt,
    int Quantity, int Guests, IReadOnlyList<TravelLineDto> Lines, decimal Amount);

public sealed record TravelBookingDto(
    Guid Id,
    string Reference,
    string Status,
    Guid TravelerId,
    string TravelerName,
    Guid VendorId,
    string VendorName,
    IReadOnlyList<TravelBookingItemDto> Items,
    decimal Total,
    decimal Fee,
    decimal Discount,
    int PointsRedeemed,
    string Currency,
    string LeadGuestName,
    string? Phone,
    string? IdNumber,
    string? SpecialRequests,
    string? Error,
    string? Address,
    string? MeetingPoint,
    Guid? ConversationId,
    string? HolderPublicKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt,
    DateTimeOffset? CheckedInAt,
    string? Notice,
    bool IsMine);

/// <summary>FR-4.4: the tax invoice, and the server's ECDSA P-256 signature over <see cref="Canonical"/>.</summary>
public sealed record TravelInvoiceDto(string Number, Guid BookingId, string Canonical, string Signature, string KeyId, string PublicKey);

public sealed record CheckInRequest(string Code);

public sealed record CheckInResultDto(string Outcome, TravelBookingDto? Booking, string Message);

public static class CheckInOutcomes
{
    public const string CheckedIn = "checked_in", AlreadyCheckedIn = "already_checked_in", NotYours = "not_yours", Invalid = "invalid", Canceled = "canceled";
}

public sealed record BindTravelKeyRequest(string PublicKey);

// ---- Vendors ----

public sealed record TravelVendorDto(Guid UserId, string Name, string? Description, string? Phone, string? Email, string Status, int FeePercent);

public sealed record SaveVendorRequest(string Name, string? Description, string? Phone, string? Email);

public sealed record TravelMeDto(TravelVendorDto? Vendor, bool CanBecomeVendor, bool IsAdmin, string Currency, IReadOnlyDictionary<string, decimal> DisplayRates);

public sealed record VendorListingDto(Guid Id, string Type, string Name, string Kind, string City, string Status, string? PhotoUrl, int UpcomingBookings);

public sealed record VendorDashboardDto(
    TravelVendorDto Vendor, IReadOnlyList<VendorListingDto> Listings, IReadOnlyList<TravelBookingDto> Bookings, decimal EarnedThisMonth, decimal FeesThisMonth);

public sealed record SetStatusRequest(string Status, string? Reason);

/// <summary>A live change to someone's bookings; their screens reload.</summary>
public sealed record TravelChangedDto(Guid BookingId, string Status, string? Notice);
