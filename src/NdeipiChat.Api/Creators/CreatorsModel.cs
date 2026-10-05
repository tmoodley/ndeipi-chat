using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Creators;

public static class CreatorsModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<BankTransfer>().Property(t => t.FeeAmount).HasPrecision(38, 6);

        model.Entity<CreatorProfile>(e =>
        {
            e.HasKey(c => c.UserId);
            e.Property(c => c.Name).HasMaxLength(CreatorsContract.MaxNameLength);
            e.Property(c => c.Category).HasMaxLength(20);
            e.Property(c => c.Bio).HasMaxLength(CreatorsContract.MaxBioLength);
            e.Property(c => c.LinksJson).HasMaxLength(2000);
            e.HasIndex(c => c.Category);
            e.HasOne<User>().WithOne().HasForeignKey<CreatorProfile>(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CreatorTier>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(40);
            e.Property(t => t.Description).HasMaxLength(500);
            e.Property(t => t.MonthlyPrice).HasPrecision(18, 2);
            e.Property(t => t.AnnualPrice).HasPrecision(18, 2);
            e.HasIndex(t => new { t.CreatorId, t.Rank });
            e.HasOne<CreatorProfile>().WithMany().HasForeignKey(t => t.CreatorId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CreatorPost>(e =>
        {
            e.Property(p => p.Title).HasMaxLength(CreatorsContract.MaxTitleLength);
            e.Property(p => p.Teaser).HasMaxLength(CreatorsContract.MaxTeaserLength);
            e.Property(p => p.Body).HasMaxLength(CreatorsContract.MaxBodyLength);
            e.Property(p => p.MediaIds).HasMaxLength(400);
            e.Property(p => p.Access).HasMaxLength(8);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.HasIndex(p => new { p.CreatorId, p.PublishAt });
            e.HasOne<CreatorProfile>().WithMany().HasForeignKey(p => p.CreatorId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CreatorMedia>(e =>
        {
            e.Property(m => m.Type).HasMaxLength(8);
            e.Property(m => m.ContentType).HasMaxLength(40);
            e.Property(m => m.FileName).HasMaxLength(200);
            e.HasIndex(m => m.CreatorId);
        });

        model.Entity<CreatorSubscription>(e =>
        {
            e.Property(s => s.Period).HasMaxLength(8);
            e.Property(s => s.Price).HasPrecision(18, 2);
            e.Property(s => s.Status).HasMaxLength(8);
            e.HasIndex(s => new { s.SubscriberId, s.CreatorId });
            e.HasIndex(s => new { s.CreatorId, s.Status });
            e.HasIndex(s => new { s.Status, s.NextAttemptAt });
            e.HasOne<User>().WithMany().HasForeignKey(s => s.SubscriberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<CreatorTier>().WithMany().HasForeignKey(s => s.TierId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<CreatorPayment>(e =>
        {
            e.Property(p => p.Kind).HasMaxLength(12);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Fee).HasPrecision(18, 2);
            e.Property(p => p.FeePercent).HasPrecision(5, 2);
            e.Property(p => p.Currency).HasMaxLength(10);
            e.Property(p => p.Status).HasMaxLength(20);
            e.HasIndex(p => p.BankTransferId).IsUnique();
            e.HasIndex(p => new { p.CreatorId, p.CreatedAt });
            e.HasIndex(p => new { p.PayerId, p.CreatedAt });
        });

        model.Entity<CreatorUnlock>(e =>
        {
            e.HasKey(u => new { u.PostId, u.UserId });
            e.HasIndex(u => u.UserId);
        });

        model.Entity<CreatorActivity>(e =>
        {
            e.Property(a => a.Kind).HasMaxLength(8);
            // Once a day per person, post (Guid.Empty for a storefront visit) and kind.
            e.HasIndex(a => new { a.CreatorId, a.UserId, a.PostId, a.Kind, a.Day }).IsUnique();
            e.HasIndex(a => new { a.CreatorId, a.Day });
        });

        model.Entity<PayoutAccount>(e =>
        {
            e.Property(a => a.Rail).HasMaxLength(8);
            e.Property(a => a.Label).HasMaxLength(100);
            e.Property(a => a.BridgeExternalAccountId).HasMaxLength(100);
            e.Property(a => a.Address).HasMaxLength(100);
            e.Property(a => a.Currency).HasMaxLength(10);
            e.HasIndex(a => a.UserId);
        });

        model.Entity<Withdrawal>(e =>
        {
            e.Property(w => w.Amount).HasPrecision(18, 2);
            e.Property(w => w.Status).HasMaxLength(12);
            e.Property(w => w.BridgeTransferId).HasMaxLength(100);
            e.Property(w => w.ProviderState).HasMaxLength(40);
            e.Property(w => w.Error).HasMaxLength(500);
            e.HasIndex(w => new { w.UserId, w.CreatedAt });
            e.HasIndex(w => w.Status);
        });
    }
}
