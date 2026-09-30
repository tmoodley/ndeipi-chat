using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Points;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Donations;

public sealed class DonationsOptions
{
    public const string Section = "Donations";

    /// <summary>FR-03's multiplier: Believe Points per whole unit given (per dollar, for USDC).</summary>
    public decimal PointsPerUnit { get; set; } = 10;
}

/// <summary>
/// Donations (SRS "Donations Micro App Module"). Organizers run campaigns (FR-02); admins mark them
/// verified. A gift (FR-01) is a Bridge transfer from the giver's wallet to the organizer's, with no
/// fee. Only once Bridge confirms it (<see cref="DonationSettlement"/>) does it count toward the
/// campaign, earn Believe Points (FR-03), get its receipt hash, and appear in the live feed (FR-04).
/// </summary>
public sealed class DonationsService(
    ChatDbContext db,
    BankingService banking,
    IOptions<BridgeOptions> bridge,
    IOptions<DonationsOptions> options,
    TimeProvider clock)
{
    DateTimeOffset Now => clock.GetUtcNow();

    static bool HasRole(User user, string role) =>
        user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(role, StringComparer.OrdinalIgnoreCase);

    public static bool IsOrganizer(User user) => HasRole(user, DonationsContract.OrganizerRole);

    public static bool IsVerifier(User user) => HasRole(user, DonationsContract.VerifierRole);

    public async Task<DonationsMeDto> MeAsync(User me, CancellationToken ct) =>
        new(IsOrganizer(me), IsVerifier(me), await db.Points.Where(p => p.UserId == me.Id).SumAsync(p => (int?)p.Points, ct) ?? 0, options.Value.PointsPerUnit);

    // ---- The feed and a campaign's page (FR-02) ----

    /// <summary>Campaigns taking gifts: verified ones first, then the newest.</summary>
    public async Task<List<CampaignDto>> FeedAsync(User me, int skip, int take, CancellationToken ct)
    {
        var now = Now;
        var campaigns = await db.DonationCampaigns.AsNoTracking().Include(c => c.Organizer)
            .Where(c => c.Status == CampaignStatuses.Active && (c.EndsAt == null || c.EndsAt > now))
            .OrderByDescending(c => c.Verified).ThenByDescending(c => c.CreatedAt)
            .Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 50)).ToListAsync(ct);
        return campaigns.Select(c => ToDto(c, me)).ToList();
    }

    public async Task<List<CampaignDto>> MineAsync(User me, CancellationToken ct) =>
        (await db.DonationCampaigns.AsNoTracking().Include(c => c.Organizer).Where(c => c.OrganizerId == me.Id)
            .OrderByDescending(c => c.CreatedAt).ToListAsync(ct)).Select(c => ToDto(c, me)).ToList();

    public async Task<CampaignDetailDto?> GetAsync(User me, Guid id, CancellationToken ct)
    {
        var campaign = await db.DonationCampaigns.AsNoTracking().Include(c => c.Organizer).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null || !CanSee(campaign, me))
            return null;
        return new CampaignDetailDto(ToDto(campaign, me), await RecentAsync(id, 20, ct), IsVerifier(me));
    }

    static bool CanSee(DonationCampaign c, User me) => c.Status != CampaignStatuses.Draft || c.OrganizerId == me.Id || IsVerifier(me);

    async Task<List<GiftActivityDto>> RecentAsync(Guid campaignId, int take, CancellationToken ct) =>
        await (from d in db.Donations.AsNoTracking()
               join u in db.Users on d.DonorId equals u.Id
               where d.CampaignId == campaignId && d.Status == DonationStatuses.Confirmed
               orderby d.ConfirmedAt descending
               select new GiftActivityDto(d.Id, d.Anonymous ? null : u.DisplayName, d.Amount, d.Message, d.ConfirmedAt!.Value))
            .Take(take).ToListAsync(ct);

    // ---- Organizers ----

    public async Task<CampaignDto> CreateAsync(User me, SaveCampaignRequest request, CancellationToken ct)
    {
        if (!IsOrganizer(me))
            throw new ChatRejectedException("Only organizers can run campaigns. Ask Ndeipi to make your account an organizer.");
        var campaign = new DonationCampaign
        {
            Id = Guid.NewGuid(),
            OrganizerId = me.Id,
            Title = "",
            Summary = "",
            Beneficiary = "",
            Icon = "",
            Currency = bridge.Value.Currency.ToLowerInvariant(),
            Status = CampaignStatuses.Draft,
            CreatedAt = Now,
        };
        Apply(campaign, request);
        db.DonationCampaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        campaign.Organizer = me;
        return ToDto(campaign, me);
    }

    public async Task<CampaignDto?> UpdateAsync(User me, Guid id, SaveCampaignRequest request, CancellationToken ct)
    {
        var campaign = await db.DonationCampaigns.Include(c => c.Organizer).FirstOrDefaultAsync(c => c.Id == id && c.OrganizerId == me.Id, ct);
        if (campaign is null)
            return null;
        if (campaign.Status == CampaignStatuses.Closed)
            throw new ChatRejectedException("This campaign has closed, so it can't be changed.");
        // A changed campaign isn't the one that was checked: it needs verifying again.
        var changed = campaign.Title != request.Title?.Trim() || campaign.Beneficiary != request.Beneficiary?.Trim();
        Apply(campaign, request);
        if (changed && campaign.Verified)
            (campaign.Verified, campaign.VerifiedAt, campaign.VerifiedById) = (false, null, null);
        await db.SaveChangesAsync(ct);
        return ToDto(campaign, me);
    }

    void Apply(DonationCampaign campaign, SaveCampaignRequest request)
    {
        campaign.Title = Required(request.Title, DonationsContract.MaxTitleLength, "A title");
        campaign.Summary = Required(request.Summary, DonationsContract.MaxSummaryLength, "A short summary");
        campaign.Story = Optional(request.Story, DonationsContract.MaxStoryLength);
        campaign.Beneficiary = Required(request.Beneficiary, 120, "Who the money is for");
        campaign.Icon = Optional(request.Icon, 16) ?? "💚";
        if (request.Target <= 0 || request.Target > 10_000_000 || Math.Round(request.Target, 2) != request.Target)
            throw new ChatRejectedException("Set a target greater than zero, to the cent.");
        campaign.Target = request.Target;
        if (request.EndsAt is { } ends && ends <= Now)
            throw new ChatRejectedException("The end date has to be in the future.");
        campaign.EndsAt = request.EndsAt;
        campaign.UpdatedAt = Now;
    }

    /// <summary>Puts a draft in the feed (<paramref name="open"/>), or closes a live campaign.</summary>
    public async Task<CampaignDto?> SetOpenAsync(User me, Guid id, bool open, CancellationToken ct)
    {
        var campaign = await db.DonationCampaigns.Include(c => c.Organizer).FirstOrDefaultAsync(c => c.Id == id && c.OrganizerId == me.Id, ct);
        if (campaign is null)
            return null;
        campaign.Status = (campaign.Status, open) switch
        {
            (CampaignStatuses.Draft, true) => CampaignStatuses.Active,
            (CampaignStatuses.Active, false) => CampaignStatuses.Closed,
            (var same, _) when same == (open ? CampaignStatuses.Active : CampaignStatuses.Closed) => same,
            _ => throw new ChatRejectedException(open ? "A closed campaign can't be reopened. Start a new one." : "Only a live campaign can be closed.")
        };
        campaign.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return ToDto(campaign, me);
    }

    /// <summary>An admin vouches for (or withdraws the badge from) a campaign.</summary>
    public async Task<CampaignDto?> VerifyAsync(User me, Guid id, bool verified, CancellationToken ct)
    {
        if (!IsVerifier(me))
            throw new ChatRejectedException("Only Ndeipi admins can verify campaigns.");
        var campaign = await db.DonationCampaigns.Include(c => c.Organizer).FirstOrDefaultAsync(c => c.Id == id, ct);
        if (campaign is null)
            return null;
        (campaign.Verified, campaign.VerifiedAt, campaign.VerifiedById) = verified ? (true, (DateTimeOffset?)Now, (Guid?)me.Id) : (false, null, null);
        campaign.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return ToDto(campaign, me);
    }

    // ---- Giving (FR-01) ----

    public async Task<DonationDto?> DonateAsync(User me, Guid campaignId, DonateRequest request, CancellationToken ct)
    {
        var campaign = await db.DonationCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null || !CanSee(campaign, me))
            return null;
        if (campaign.Status != CampaignStatuses.Active || campaign.EndsAt <= Now)
            throw new ChatRejectedException("This campaign isn't taking gifts.");
        if (campaign.OrganizerId == me.Id)
            throw new ChatRejectedException("You can't give to your own campaign.");
        var settings = bridge.Value;
        if (!settings.IsConfigured)
            throw new ChatRejectedException("Giving isn't set up on this server.");
        if (request.Amount <= 0 || Math.Round(request.Amount, 2) != request.Amount)
            throw new ChatRejectedException("Enter an amount greater than zero, to the cent.");
        if (request.Amount > settings.MaxTransferAmount)
            throw new ChatRejectedException($"The most you can give at once is {Amounts.Format(settings.MaxTransferAmount)} {campaign.Currency.ToUpperInvariant()}.");
        var profiles = await db.BankingProfiles.AsNoTracking().Where(p => p.UserId == me.Id || p.UserId == campaign.OrganizerId).ToListAsync(ct);
        if (profiles.FirstOrDefault(p => p.UserId == me.Id) is not { CanTransfer: true })
            throw new ChatRejectedException("Verify your identity under Me > Wallet before giving.");
        if (profiles.FirstOrDefault(p => p.UserId == campaign.OrganizerId) is not { CanTransfer: true })
            throw new ChatRejectedException("This campaign can't take gifts right now: its organizer's wallet isn't ready.");

        var transfer = new BankTransfer
        {
            Id = Guid.NewGuid(),
            SenderId = me.Id,
            RecipientId = campaign.OrganizerId,
            Amount = request.Amount,
            Currency = campaign.Currency,
            Memo = $"Gift: {(campaign.Title.Length > 60 ? campaign.Title[..60] : campaign.Title)}",
            CreatedAt = Now,
            UpdatedAt = Now
        };
        var donation = new Donation
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            DonorId = me.Id,
            Amount = request.Amount,
            Currency = campaign.Currency,
            Anonymous = request.Anonymous,
            Message = Optional(request.Message, DonationsContract.MaxMessageLength),
            Status = DonationStatuses.Pending,
            BankTransferId = transfer.Id,
            CreatedAt = Now
        };
        db.BankTransfers.Add(transfer);
        db.Donations.Add(donation);
        await db.SaveChangesAsync(ct);

        await banking.SubmitTransferAsync(transfer.Id, ct);
        return await DonationAsync(me, donation.Id, ct);
    }

    public async Task<List<DonationDto>> MyDonationsAsync(User me, CancellationToken ct)
    {
        var ids = await db.Donations.Where(d => d.DonorId == me.Id).OrderByDescending(d => d.CreatedAt).Select(d => d.Id).Take(100).ToListAsync(ct);
        var result = new List<DonationDto>();
        foreach (var id in ids)
            result.Add((await DonationAsync(me, id, ct))!);
        return result;
    }

    public async Task<DonationDto?> DonationAsync(User me, Guid id, CancellationToken ct) =>
        await (from d in db.Donations.AsNoTracking()
               join c in db.DonationCampaigns on d.CampaignId equals c.Id
               join t in db.BankTransfers on d.BankTransferId equals t.Id
               where d.Id == id && d.DonorId == me.Id
               select new DonationDto(d.Id, c.Id, c.Title, c.Beneficiary, d.Amount, d.Currency, d.Anonymous, d.Message, d.Status, d.Error,
                   d.Points, t.BridgeTransferId, d.ReceiptHash, d.CreatedAt, d.ConfirmedAt))
            .FirstOrDefaultAsync(ct);

    /// <summary>Anyone can check a receipt's hash: it's either a gift the server recorded, or not.</summary>
    public async Task<ReceiptCheckDto> CheckReceiptAsync(string hash, CancellationToken ct)
    {
        hash = hash.Trim().ToLowerInvariant();
        var found = await (from d in db.Donations.AsNoTracking()
                           join c in db.DonationCampaigns on d.CampaignId equals c.Id
                           where d.ReceiptHash == hash
                           select new ReceiptCheckDto(true, c.Title, d.Amount, d.Currency, d.ConfirmedAt, d.Points)).FirstOrDefaultAsync(ct);
        return found ?? new ReceiptCheckDto(false, null, null, null, null, null);
    }

    // ---- Helpers ----

    static CampaignDto ToDto(DonationCampaign c, User me) => new(
        c.Id, c.Title, c.Summary, c.Story, c.Beneficiary, c.Icon, c.Organizer.DisplayName, c.Target, c.Raised, c.Gifts, c.Currency,
        c.Verified, c.Status, c.EndsAt, c.CreatedAt, c.OrganizerId == me.Id);

    static string Required(string? text, int max, string what) =>
        Optional(text, max) ?? throw new ChatRejectedException($"{what} is needed.");

    static string? Optional(string? text, int max)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        return text.Length <= max ? text : throw new ChatRejectedException($"Keep it under {max} characters.");
    }
}

/// <summary>Moves a gift along as its Bridge transfer is confirmed or fails.</summary>
public sealed class DonationPaymentListener(DonationSettlement donations) : IBankTransferListener
{
    public Task TransferChangedAsync(BankTransfer transfer, CancellationToken ct) => transfer.Status switch
    {
        TransferStatuses.Confirmed => donations.ConfirmAsync(transfer, ct),
        TransferStatuses.Failed => donations.FailAsync(transfer, ct),
        _ => Task.CompletedTask
    };
}

/// <summary>Anyone signed in may follow a live campaign's progress; a draft, only its organizer.</summary>
public sealed class DonationTopicPolicy(ChatDbContext db) : ITopicPolicy
{
    public string Prefix => "donations";

    public async Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Guid.TryParseExact(key, "N", out var id)
        && await db.DonationCampaigns.AnyAsync(c => c.Id == id && (c.Status != CampaignStatuses.Draft || c.OrganizerId == user.Id), ct);
}
