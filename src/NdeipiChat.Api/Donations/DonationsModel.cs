using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Donations;

/// <summary>The Donations tables, and the shared Believe Points ledger.</summary>
public static class DonationsModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<DonationCampaign>(e =>
        {
            e.Property(c => c.Title).HasMaxLength(DonationsContract.MaxTitleLength);
            e.Property(c => c.Summary).HasMaxLength(DonationsContract.MaxSummaryLength);
            e.Property(c => c.Story).HasMaxLength(DonationsContract.MaxStoryLength);
            e.Property(c => c.Beneficiary).HasMaxLength(120);
            e.Property(c => c.Icon).HasMaxLength(16);
            e.Property(c => c.Currency).HasMaxLength(8);
            e.Property(c => c.Status).HasMaxLength(16);
            e.Property(c => c.Target).HasPrecision(18, 2);
            e.Property(c => c.Raised).HasPrecision(18, 2);
            e.HasIndex(c => new { c.Status, c.Verified, c.CreatedAt });
            e.HasIndex(c => c.OrganizerId);
            e.HasOne(c => c.Organizer).WithMany().HasForeignKey(c => c.OrganizerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Donation>(e =>
        {
            e.Property(d => d.Amount).HasPrecision(18, 2);
            e.Property(d => d.Currency).HasMaxLength(8);
            e.Property(d => d.Message).HasMaxLength(DonationsContract.MaxMessageLength);
            e.Property(d => d.Status).HasMaxLength(16);
            e.Property(d => d.Error).HasMaxLength(300);
            e.Property(d => d.ReceiptHash).HasMaxLength(64);
            e.HasIndex(d => d.BankTransferId).IsUnique();
            e.HasIndex(d => d.ReceiptHash).IsUnique().HasFilter("[ReceiptHash] IS NOT NULL");
            e.HasIndex(d => new { d.CampaignId, d.Status, d.ConfirmedAt });
            e.HasIndex(d => new { d.DonorId, d.CreatedAt });
            e.HasOne<DonationCampaign>().WithMany().HasForeignKey(d => d.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(d => d.DonorId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<PointsEntry>(e =>
        {
            e.Property(p => p.Source).HasMaxLength(32);
            e.Property(p => p.SourceId).HasMaxLength(64);
            e.Property(p => p.Description).HasMaxLength(200);
            e.HasIndex(p => new { p.Source, p.SourceId }).IsUnique();
            e.HasIndex(p => new { p.UserId, p.CreatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
