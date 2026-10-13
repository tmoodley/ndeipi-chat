namespace NdeipiChat.Api.Travel;

/// <summary>A lodge, hotel or tour operator selling on Lets Travel. One per user; they must have passed KYC.</summary>
public sealed class TravelVendor
{
    public Guid UserId { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }

    /// <summary>"active", or "suspended" by an admin (nothing they list can be booked).</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A place to stay (SRS TravelProperty).</summary>
public sealed class TravelProperty
{
    public Guid Id { get; set; }
    public Guid VendorId { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }
    public string? Description { get; set; }
    public required string City { get; set; }
    public required string Country { get; set; }
    public string? Address { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public int? Stars { get; set; }

    /// <summary>Amenity codes, comma-separated with leading and trailing commas (",wifi,pool,") so one can be matched with LIKE.</summary>
    public string Amenities { get; set; } = ",";

    public required string Cancellation { get; set; }
    public string CheckInTime { get; set; } = "14:00";
    public string CheckOutTime { get; set; } = "10:00";

    /// <summary>"draft", "published" or "suspended" (by an admin).</summary>
    public string Status { get; set; } = "draft";

    /// <summary>Photo IDs in order, comma-separated.</summary>
    public string PhotoIds { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A kind of room at a property and how many of it there are (SRS RoomInventory).</summary>
public sealed class RoomType
{
    public Guid Id { get; set; }
    public Guid PropertyId { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public required string BoardPlan { get; set; }
    public int Capacity { get; set; }

    /// <summary>How many of these rooms the property sells each night.</summary>
    public int Units { get; set; }

    public decimal NightlyPrice { get; set; }
    public string? Description { get; set; }
    public bool Archived { get; set; }

    /// <summary>NFR-3: vendor edits are optimistic; a stale edit is refused rather than overwriting another.</summary>
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>
/// One room type on one night: what's held and booked, a price for that night if it differs, and
/// whether the vendor stopped selling it. Rows exist only for nights someone booked or the vendor set;
/// a missing night is all units free at the base price. Holds change these counts with one conditional
/// UPDATE per night inside a transaction, so the last room can only be taken once (NFR-2, NFR-3).
/// </summary>
public sealed class RoomNight
{
    public Guid RoomTypeId { get; set; }
    public DateOnly Date { get; set; }
    public int Held { get; set; }
    public int Booked { get; set; }
    public decimal? Price { get; set; }
    public bool Closed { get; set; }
}

/// <summary>A guided tour or excursion (SRS TourPackage).</summary>
public sealed class TourPackage
{
    public Guid Id { get; set; }
    public Guid VendorId { get; set; }
    public required string Title { get; set; }
    public required string Kind { get; set; }
    public string? Description { get; set; }
    public required string City { get; set; }
    public required string Country { get; set; }
    public double? Lat { get; set; }
    public double? Lng { get; set; }
    public double DurationHours { get; set; }

    /// <summary>Seats per departure, per vehicle or guide.</summary>
    public int MaxCapacity { get; set; }

    public required string Difficulty { get; set; }
    public decimal PricePerPerson { get; set; }

    /// <summary>FR-3.3: river cruises, flights and the like, which the weather may move.</summary>
    public bool WeatherDependent { get; set; }

    public string? Includes { get; set; }
    public string? MeetingPoint { get; set; }
    public string Status { get; set; } = "draft";
    public string PhotoIds { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>FR-3.2: park fees, equipment, gratuities and transfers, chosen at booking (mandatory ones always).</summary>
public sealed class TourAddOn
{
    public Guid Id { get; set; }
    public Guid TourId { get; set; }
    public required string Name { get; set; }
    public decimal Price { get; set; }
    public bool PerPerson { get; set; }
    public bool Mandatory { get; set; }
    public bool Archived { get; set; }
}

/// <summary>One departure of a tour with its seats (SRS TourSchedule).</summary>
public sealed class TourDeparture
{
    public Guid Id { get; set; }
    public Guid TourId { get; set; }
    public DateTimeOffset StartsAt { get; set; }
    public int Seats { get; set; }
    public int Held { get; set; }
    public int Booked { get; set; }
    public string? Guide { get; set; }
    public string Status { get; set; } = "scheduled";
    public string? Note { get; set; }
    public byte[] RowVersion { get; set; } = [];
}

/// <summary>A room or seats set aside while the traveller pays (FR-2.3), for 15 minutes.</summary>
public sealed class TravelHold
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string ItemType { get; set; }

    /// <summary>The room type or departure.</summary>
    public Guid ReferenceId { get; set; }

    public Guid VendorId { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public int Quantity { get; set; }
    public int Guests { get; set; }

    /// <summary>The price lines at hold time, as JSON, so the price can't move while paying.</summary>
    public required string LinesJson { get; set; }

    public decimal Amount { get; set; }

    /// <summary>"active", "paying", "converted" or "released".</summary>
    public string Status { get; set; } = "active";

    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A booking (SRS BookingOrder): paid from the wallet, with its voucher and invoice.</summary>
public sealed class TravelBooking
{
    public Guid Id { get; set; }

    /// <summary>What the traveller and the vendor quote: "LT-" and six characters.</summary>
    public required string Reference { get; set; }

    public Guid TravelerId { get; set; }
    public Guid VendorId { get; set; }
    public Guid HoldId { get; set; }
    public string Status { get; set; } = "pending_payment";
    public decimal Amount { get; set; }
    public decimal Fee { get; set; }

    /// <summary>FR-4.3: taken off the price for Believe Points, out of Ndeipi's fee.</summary>
    public decimal Discount { get; set; }

    public int PointsRedeemed { get; set; }
    public required string Currency { get; set; }
    public Guid? BankTransferId { get; set; }
    public Guid? RefundTransferId { get; set; }
    public required string LeadGuestName { get; set; }
    public string? Phone { get; set; }

    /// <summary>Passport or ID number for park permits, encrypted at rest (NFR-4).</summary>
    public string? IdNumberProtected { get; set; }

    public string? SpecialRequests { get; set; }
    public string? Error { get; set; }

    /// <summary>The latest word from the vendor: a rescheduled or cancelled departure (FR-3.3).</summary>
    public string? Notice { get; set; }

    /// <summary>FR-5.3: the chat with the vendor, opened on confirmation.</summary>
    public Guid? ConversationId { get; set; }

    /// <summary>The traveller's device key that signs the offline voucher QR (FR-5.1).</summary>
    public string? HolderPublicKey { get; set; }

    public string? InvoiceNumber { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public DateTimeOffset? CheckedInAt { get; set; }
    public string? CheckedInBy { get; set; }
    public DateTimeOffset? CanceledAt { get; set; }
}

/// <summary>What a booking is for (SRS BookingItem): a stay or a tour departure, priced at booking.</summary>
public sealed class TravelBookingItem
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public required string ItemType { get; set; }
    public Guid ReferenceId { get; set; }

    /// <summary>The property or tour, for searching a vendor's bookings.</summary>
    public Guid ListingId { get; set; }

    public required string Title { get; set; }
    public required string Subtitle { get; set; }
    public DateOnly? CheckIn { get; set; }
    public DateOnly? CheckOut { get; set; }
    public DateTimeOffset? StartsAt { get; set; }
    public int Quantity { get; set; }
    public int Guests { get; set; }
    public required string LinesJson { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>A listing photo, re-encoded and served only through an expiring link.</summary>
public sealed class TravelPhoto
{
    public Guid Id { get; set; }
    public Guid VendorId { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The key Lets Travel signs invoices with (FR-4.4): ECDSA P-256, created on first use. The private
/// half is encrypted with the server's Data Protection keys; the public half is published by ID so
/// anyone holding an invoice can check it.
/// </summary>
public sealed class TravelSigningKey
{
    public required string Id { get; set; }
    public required string PublicKey { get; set; }
    public required string PrivateKeyProtected { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
