namespace NdeipiChat.Contracts;

/// <summary>
/// The Donations sub-app (SRS "Ndeipi Super App: Donations Micro App Module"): organizers publish
/// fundraising campaigns (FR-02), anyone gives in a tap from their wallet (FR-01), givers earn
/// Believe Points (FR-03), and each campaign shows live progress and recent gifts, with a receipt
/// for every gift (FR-04). There are no fees: the whole gift goes to the campaign.
/// </summary>
public static class DonationsContract
{
    public const string AppId = "donations";
    public const string BasePath = "api/donations";

    /// <summary>Who may run campaigns: organizers, the same role that runs events.</summary>
    public const string OrganizerRole = EventsContract.OrganizerRole;

    /// <summary>Who may mark a campaign verified (the badge on its card): platform admins.</summary>
    public const string VerifierRole = "admin";

    /// <summary>FR-01: the quick-select amounts.</summary>
    public static readonly IReadOnlyList<decimal> QuickAmounts = [1m, 5m, 10m, 25m];

    public const int MaxTitleLength = 120;
    public const int MaxSummaryLength = 280;
    public const int MaxStoryLength = 4000;
    public const int MaxMessageLength = 140;

    /// <summary>Live updates for a campaign: its totals and each confirmed gift. "donations:{campaignId:N}".</summary>
    public static string Topic(Guid campaignId) => $"donations:{campaignId:N}";
}

public static class CampaignStatuses
{
    /// <summary>Being written: only its organizer sees it.</summary>
    public const string Draft = "draft";

    /// <summary>In the feed, taking gifts.</summary>
    public const string Active = "active";

    /// <summary>Finished: still visible, no longer taking gifts.</summary>
    public const string Closed = "closed";
}

public static class DonationStatuses
{
    /// <summary>Sent to Bridge; not counted until it's confirmed.</summary>
    public const string Pending = "pending";

    public const string Confirmed = "confirmed";
    public const string Failed = "failed";
}

/// <param name="Raised">Confirmed gifts only.</param>
/// <param name="Gifts">How many confirmed gifts.</param>
/// <param name="Currency">What gifts are paid in, e.g. "usdc".</param>
/// <param name="IsMine">The signed-in user organizes it.</param>
public sealed record CampaignDto(
    Guid Id,
    string Title,
    string Summary,
    string? Story,
    string Beneficiary,
    string Icon,
    string OrganizerName,
    decimal Target,
    decimal Raised,
    int Gifts,
    string Currency,
    bool Verified,
    string Status,
    DateTimeOffset? EndsAt,
    DateTimeOffset CreatedAt,
    bool IsMine);

public sealed record SaveCampaignRequest(
    string Title,
    string Summary,
    string? Story,
    string Beneficiary,
    string? Icon,
    decimal Target,
    DateTimeOffset? EndsAt);

/// <summary>A campaign's page: the campaign, the latest gifts, and whether the viewer may verify it.</summary>
public sealed record CampaignDetailDto(CampaignDto Campaign, IReadOnlyList<GiftActivityDto> Recent, bool CanVerify);

/// <summary>One gift in a campaign's activity feed (FR-04). Anonymous gifts have no name.</summary>
public sealed record GiftActivityDto(Guid Id, string? DonorName, decimal Amount, string? Message, DateTimeOffset At);

/// <summary>What a campaign's live topic carries when a gift is confirmed.</summary>
public sealed record CampaignUpdateDto(Guid CampaignId, decimal Raised, int Gifts, GiftActivityDto Gift);

/// <param name="Anonymous">Leave the giver's name off the campaign's activity feed. The organizer doesn't see it either.</param>
public sealed record DonateRequest(decimal Amount, bool Anonymous, string? Message);

/// <summary>A gift, as its giver sees it: with its receipt once confirmed.</summary>
/// <param name="TransferReference">Bridge's reference for the transfer.</param>
/// <param name="ReceiptHash">SHA-256 of the receipt (<see cref="DonationReceipt.Canonical"/>); anyone can check it against the server.</param>
public sealed record DonationDto(
    Guid Id,
    Guid CampaignId,
    string CampaignTitle,
    string Beneficiary,
    decimal Amount,
    string Currency,
    bool Anonymous,
    string? Message,
    string Status,
    string? Error,
    int Points,
    string? TransferReference,
    string? ReceiptHash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ConfirmedAt);

/// <summary>What checking a receipt's hash finds: the gift it belongs to (without who gave it).</summary>
public sealed record ReceiptCheckDto(bool Valid, string? CampaignTitle, decimal? Amount, string? Currency, DateTimeOffset? ConfirmedAt, int? Points);

/// <summary>
/// A gift's receipt, as the text its hash is taken over. The server computes it when the gift is
/// confirmed and never changes it, so a receipt that matches a stored hash is exactly what was recorded.
/// </summary>
public static class DonationReceipt
{
    public static string Canonical(Guid donationId, Guid campaignId, decimal amount, string currency, string transferReference, DateTimeOffset confirmedAt, int points) =>
        string.Join('|', "ndeipi-donation-v1", donationId.ToString("N"), campaignId.ToString("N"),
            amount.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), currency.ToLowerInvariant(),
            transferReference, confirmedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"), points.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static string Hash(string canonical) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
}

// ---- Believe Points, shared across Ndeipi ----

/// <summary>
/// Believe Points: Ndeipi's reward points (Donations SRS FR-03). One balance per person, shared by
/// every app. An app awards points on the server for something that happened (a confirmed gift),
/// never on the device's word.
/// </summary>
public static class PointsContract
{
    public const string BasePath = "api/points";
    public const string Name = "Believe Points";
}

/// <param name="Source">The app that awarded it, e.g. "donations".</param>
public sealed record PointsEntryDto(string Source, string Description, int Points, DateTimeOffset At);

public sealed record PointsDto(int Balance, IReadOnlyList<PointsEntryDto> Recent);

/// <summary>The signed-in person, as Donations sees them: what they may do, and their points.</summary>
/// <param name="PointsPerUnit">Believe Points a gift earns per whole unit given.</param>
public sealed record DonationsMeDto(bool IsOrganizer, bool IsVerifier, int Points, decimal PointsPerUnit);
