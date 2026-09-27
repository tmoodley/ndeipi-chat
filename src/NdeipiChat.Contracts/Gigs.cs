namespace NdeipiChat.Contracts;

/// <summary>
/// Gigs (the Gigs sub-app, SRS "Ndeipi Gigs Module"): worker profiles pinned to a place, gigs
/// dropped on a map or started from a chat, proximity dispatch to available workers, a shared
/// gig-and-chat workspace, and settlement in NdeipiCoin straight from client to worker.
/// </summary>
public static class GigsContract
{
    public const string AppId = "gigs";
    public const string BasePath = "api/gigs";

    /// <summary>How far dispatch looks for workers (SRS FR-03).</summary>
    public const double DispatchRadiusKm = 50;

    /// <summary>How many of the nearest suitable workers each gig is offered to at once.</summary>
    public const int DispatchFanOut = 5;

    /// <summary>
    /// Where other people see a worker, or a gig before it's theirs: rounded to 2 decimal places,
    /// about a kilometre. Exact positions are only shared between a gig's client and its worker.
    /// </summary>
    public const int PublicPrecision = 2;

    public const int MaxSkills = 5;

    /// <summary>Everyone using Gigs: available workers appearing and disappearing on the map.</summary>
    public const string MapTopic = "gigs-map:all";

    /// <summary>One person's gig news: offers, and changes to gigs they're part of.</summary>
    public static string UserTopic(Guid userId) => $"gigs:{userId:N}";

    public static double Approximate(double degrees) => Math.Round(degrees, PublicPrecision);

    /// <summary>Great-circle distance in kilometres.</summary>
    public static double DistanceKm(double lat1, double lng1, double lat2, double lng2)
    {
        const double earthRadiusKm = 6371.0088;
        static double Rad(double d) => d * Math.PI / 180;
        var dLat = Rad(lat2 - lat1);
        var dLng = Rad(lng2 - lng1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * earthRadiusKm * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }
}

public static class GigSkills
{
    public static readonly IReadOnlyList<string> All =
        ["delivery", "photography", "video", "design", "development", "construction", "cleaning", "agriculture", "tutoring", "events", "other"];

    public static string Label(string skill) => skill switch
    {
        "delivery" => "Delivery & errands",
        "photography" => "Photography",
        "video" => "Video",
        "design" => "Design",
        "development" => "Software development",
        "construction" => "Building & repairs",
        "cleaning" => "Cleaning",
        "agriculture" => "Farm work",
        "tutoring" => "Tutoring",
        "events" => "Event staff",
        "other" => "Other",
        _ => skill
    };
}

public static class GigStatuses
{
    /// <summary>Posted; offered to nearby workers, waiting for one to take it.</summary>
    public const string Open = "Open";

    /// <summary>A worker took it; they and the client work it out in the gig's chat.</summary>
    public const string Assigned = "Assigned";

    /// <summary>The worker says it's done; waiting for the client to approve and pay.</summary>
    public const string Submitted = "Submitted";

    public const string Completed = "Completed";
    public const string Cancelled = "Cancelled";
}

public static class GigOfferStatuses
{
    public const string Offered = "Offered";
    public const string Accepted = "Accepted";
    public const string Declined = "Declined";

    /// <summary>Someone else took the gig, or it was cancelled.</summary>
    public const string Closed = "Closed";
}

/// <param name="Latitude">Exact for your own profile; <see cref="GigsContract.Approximate"/> for anyone else's.</param>
/// <param name="Rating">Average stars out of 5, or null before the first rating.</param>
public sealed record GigProfileDto(
    Guid UserId,
    string DisplayName,
    string? AvatarUrl,
    string Headline,
    IReadOnlyList<string> Skills,
    string? Region,
    double Latitude,
    double Longitude,
    bool IsAvailable,
    int CompletedGigs,
    double? Rating,
    int RatingCount);

public sealed record SaveGigProfileRequest(string Headline, IReadOnlyList<string> Skills, string? Region, double Latitude, double Longitude);

/// <param name="Latitude">Where the worker is now, if the device knows; otherwise the profile's place is used.</param>
public sealed record AvailabilityRequest(bool IsAvailable, double? Latitude = null, double? Longitude = null);

/// <summary>A worker on the map: approximate position only.</summary>
public sealed record MapWorkerDto(Guid UserId, string DisplayName, IReadOnlyList<string> Skills, double Latitude, double Longitude, double? Rating, bool IsAvailable);

public sealed record GigMapDto(IReadOnlyList<MapWorkerDto> Workers, IReadOnlyList<GigDto> MyGigs);

/// <param name="Budget">Decimal string in <paramref name="TokenSymbol"/>.</param>
/// <param name="Latitude">Exact for the gig's client and worker; approximate for workers it's offered to.</param>
/// <param name="DistanceKm">From the viewer's profile, for workers.</param>
/// <param name="MyOffer">For a worker: their offer's status (see <see cref="GigOfferStatuses"/>), if they have one.</param>
/// <param name="PaymentStatus">The NdeipiCoin transfer's status once the client has paid (see TransferStatuses).</param>
public sealed record GigDto(
    Guid Id,
    string Title,
    string Description,
    string Skill,
    double Latitude,
    double Longitude,
    string? Region,
    string Budget,
    string TokenSymbol,
    string Status,
    UserDto Client,
    UserDto? Worker,
    Guid? ConversationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    string? PaymentStatus,
    double? DistanceKm,
    bool IsClient,
    bool IsWorker,
    string? MyOffer,
    int? WorkerStars,
    int? ClientStars);

/// <param name="ConversationId">The chat it was started from, if any. In a direct chat, the gig is offered to the other person.</param>
/// <param name="WorkerId">Offer it to this worker only, e.g. one picked on the map, instead of dispatching.</param>
public sealed record CreateGigRequest(
    string Title,
    string? Description,
    string Skill,
    double Latitude,
    double Longitude,
    string? Region,
    string Budget,
    Guid? ConversationId = null,
    Guid? WorkerId = null);

public sealed record RateGigRequest(int Stars);

/// <summary>Sent on <see cref="GigsContract.UserTopic"/>: an offer ("offer"), or a change to one of your gigs ("gig").</summary>
public sealed record GigNewsDto(string Kind, GigDto Gig);
