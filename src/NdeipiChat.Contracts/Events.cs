using System.Text;

namespace NdeipiChat.Contracts;

/// <summary>
/// Events and ticketing (the Events sub-app, SRS "Ndeipi.Modules.Events", sprint 1): a catalog by
/// city and category with live availability, 10-minute seat holds, checkout through the Ndeipi
/// wallet (Bridge), tickets with a signed rolling QR code, and an offline-capable gate scanner.
/// </summary>
public static class EventsContract
{
    public const string AppId = "events";
    public const string BasePath = "api/events";
    public const string OrganizerRole = "organizer";

    /// <summary>How long a hold keeps seats out of sale while the buyer pays (SRS FR-2.1).</summary>
    public static readonly TimeSpan HoldDuration = TimeSpan.FromMinutes(10);

    public const int MaxTicketsPerHold = 10;

    /// <summary>A ticket's QR code changes every 15 seconds (SRS FR-5.1).</summary>
    public const int QrWindowSeconds = 15;

    /// <summary>How many windows either side of "now" a gate accepts, for clocks that drift.</summary>
    public const int QrWindowTolerance = 2;

    /// <summary>Realtime topic for an event's availability.</summary>
    public static string Topic(Guid eventId) => $"events:{eventId:N}";

    public static long WindowAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / QrWindowSeconds;

    /// <summary>
    /// What the holder's device signs for one QR window. The key never leaves the device; gates
    /// check the signature against the public key the holder registered, so a screenshot is stale
    /// within seconds and a copied code can't be regenerated.
    /// </summary>
    public static byte[] QrPayload(Guid ticketId, long window) => Encoding.UTF8.GetBytes($"ndeipi-ticket-v1\n{ticketId:N}\n{window}");

    public static string QrText(Guid ticketId, long window, string signatureBase64) => $"NDT1:{ticketId:N}:{window}:{signatureBase64}";

    public static bool TryParseQr(string? text, out Guid ticketId, out long window, out string signature)
    {
        (ticketId, window, signature) = (Guid.Empty, 0, "");
        var parts = text?.Trim().Split(':');
        return parts is ["NDT1", var id, var w, var s]
            && Guid.TryParseExact(id, "N", out ticketId)
            && long.TryParse(w, out window)
            && (signature = s).Length > 0;
    }
}

public static class EventCategories
{
    public const string Music = "music";
    public const string Business = "business";
    public const string Sports = "sports";
    public const string Comedy = "comedy";
    public const string Culture = "culture";

    public static readonly IReadOnlyList<string> All = [Music, Business, Sports, Comedy, Culture];

    public static string Label(string category) => category switch
    {
        Music => "Music & Live Concerts",
        Business => "Business Summits & Conferences",
        Sports => "Sports",
        Comedy => "Stand-up Comedy",
        Culture => "Cultural Gatherings",
        _ => category
    };
}

public static class HoldStatuses
{
    public const string Active = "Active";
    public const string Paying = "Paying";
    public const string Converted = "Converted";
    public const string Released = "Released";
}

public static class OrderStatuses
{
    public const string Pending = "Pending";
    public const string Paid = "Paid";
    public const string Failed = "Failed";
}

public static class TicketStatuses
{
    public const string Valid = "Valid";
    public const string Admitted = "Admitted";
    public const string Revoked = "Revoked";
}

public static class AdmissionOutcomes
{
    public const string Admitted = "Admitted";
    public const string AlreadyAdmitted = "AlreadyAdmitted";
    public const string Unknown = "Unknown";
    public const string Revoked = "Revoked";
}

/// <param name="Price">Decimal string in the event's currency; "0" for free.</param>
public sealed record TicketTierDto(Guid Id, string Name, string Price, int Capacity, int Available);

public sealed record EventSummaryDto(
    Guid Id,
    string Title,
    string Category,
    string City,
    string Venue,
    DateTimeOffset StartsAt,
    string Currency,
    string? LowestPrice,
    int Available);

public sealed record EventPageDto(IReadOnlyList<EventSummaryDto> Events, int Total, IReadOnlyList<string> Cities);

public sealed record EventDetailDto(
    Guid Id,
    string Title,
    string Description,
    string Category,
    string City,
    string Venue,
    DateTimeOffset StartsAt,
    string Currency,
    IReadOnlyList<TicketTierDto> Tiers,
    UserDto Organizer,
    bool IsPublished,
    bool CanManage,
    bool CanScan);

public sealed record SaveTierRequest(Guid? Id, string Name, string Price, int Capacity);

public sealed record SaveEventRequest(
    string Title,
    string? Description,
    string Category,
    string City,
    string Venue,
    DateTimeOffset StartsAt,
    IReadOnlyList<SaveTierRequest> Tiers);

public sealed record HoldRequest(Guid TierId, int Quantity);

public sealed record HoldDto(Guid Id, Guid EventId, Guid TierId, string TierName, int Quantity, string Amount, string Currency, DateTimeOffset ExpiresAt, string Status);

public sealed record TicketDto(
    Guid Id,
    Guid EventId,
    string EventTitle,
    string TierName,
    DateTimeOffset StartsAt,
    string Venue,
    string City,
    string Status,
    string? HolderPublicKey);

public sealed record OrderDto(Guid Id, Guid EventId, string Status, string Amount, string Currency, string? Error, IReadOnlyList<TicketDto> Tickets);

/// <summary>The public half of the key this device signs the ticket's QR codes with (base64 SPKI, P-256).</summary>
public sealed record BindHolderKeyRequest(string PublicKeySpki);

/// <summary>What a gate device keeps to admit people offline (SRS FR-5.2): no secrets, only public keys.</summary>
public sealed record GateTicketDto(Guid TicketId, string? HolderPublicKey, string Status, string TierName, string HolderName, DateTimeOffset? AdmittedAt = null);

public sealed record GateListDto(Guid EventId, string Title, DateTimeOffset SyncedAt, IReadOnlyList<GateTicketDto> Tickets);

public sealed record AdmissionDto(Guid TicketId, DateTimeOffset AdmittedAt);

public sealed record AdmissionsRequest(IReadOnlyList<AdmissionDto> Admissions, string DeviceName);

/// <param name="FirstAdmittedAt">For a ticket already admitted: when, as the server first recorded it.</param>
public sealed record AdmissionResultDto(Guid TicketId, string Outcome, DateTimeOffset? FirstAdmittedAt);

public sealed record AddValidatorRequest(string Email);

public sealed record TierAvailabilityDto(Guid TierId, int Available);

/// <summary>Sent on <see cref="EventsContract.Topic"/> whenever tickets are held, sold or released (SRS FR-1.3).</summary>
public sealed record EventAvailabilityDto(Guid EventId, IReadOnlyList<TierAvailabilityDto> Tiers);
