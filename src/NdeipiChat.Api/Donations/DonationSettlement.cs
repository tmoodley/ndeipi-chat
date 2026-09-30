using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Points;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Donations;

/// <summary>
/// What happens when Bridge answers for a gift (FR-03, FR-04). Kept apart from DonationsService,
/// which submits transfers, because Bridge's answer comes back through the banking service.
/// </summary>
public sealed class DonationSettlement(ChatDbContext db, PointsService points, ChatNotifier notifier, IOptions<DonationsOptions> options, TimeProvider clock)
{
    /// <summary>The gift arrived: count it, award its points, fix its receipt, and tell everyone watching.</summary>
    public async Task ConfirmAsync(BankTransfer transfer, CancellationToken ct)
    {
        var donation = await db.Donations.AsNoTracking().FirstOrDefaultAsync(d => d.BankTransferId == transfer.Id, ct);
        if (donation is null || donation.Status != DonationStatuses.Pending)
            return;
        var confirmedAt = clock.GetUtcNow();
        var earned = (int)Math.Floor(donation.Amount * options.Value.PointsPerUnit);
        var hash = DonationReceipt.Hash(DonationReceipt.Canonical(donation.Id, donation.CampaignId, donation.Amount, donation.Currency,
            transfer.BridgeTransferId ?? transfer.Id.ToString("N"), confirmedAt, earned));

        // Only one confirmation counts, however many times Bridge tells us.
        var confirmed = await db.Donations.Where(d => d.Id == donation.Id && d.Status == DonationStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DonationStatuses.Confirmed)
                .SetProperty(d => d.ConfirmedAt, confirmedAt)
                .SetProperty(d => d.Points, earned)
                .SetProperty(d => d.ReceiptHash, hash), ct);
        if (confirmed == 0)
            return;
        await db.DonationCampaigns.Where(c => c.Id == donation.CampaignId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Raised, c => c.Raised + donation.Amount).SetProperty(c => c.Gifts, c => c.Gifts + 1), ct);

        var campaign = await db.DonationCampaigns.AsNoTracking().FirstAsync(c => c.Id == donation.CampaignId, ct);
        await points.AwardAsync(donation.DonorId, earned, DonationsContract.AppId, donation.Id.ToString("N"), $"Gift to {campaign.Title}", ct);

        var donor = donation.Anonymous ? null : await db.Users.Where(u => u.Id == donation.DonorId).Select(u => u.DisplayName).FirstAsync(ct);
        await notifier.PublishAsync(DonationsContract.Topic(campaign.Id),
            new CampaignUpdateDto(campaign.Id, campaign.Raised, campaign.Gifts, new GiftActivityDto(donation.Id, donor, donation.Amount, donation.Message, confirmedAt)));
    }

    public async Task FailAsync(BankTransfer transfer, CancellationToken ct) =>
        await db.Donations.Where(d => d.BankTransferId == transfer.Id && d.Status == DonationStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(d => d.Status, DonationStatuses.Failed)
                .SetProperty(d => d.Error, transfer.Error ?? "The gift didn't go through."), ct);
}
