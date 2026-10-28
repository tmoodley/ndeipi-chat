using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;

namespace NdeipiChat.Api.Rwa;

public static class RwaModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<RwaAsset>(e =>
        {
            e.Property(a => a.Code).HasMaxLength(32);
            e.HasIndex(a => a.Code).IsUnique();
            e.Property(a => a.Name).HasMaxLength(120);
            e.Property(a => a.Location).HasMaxLength(120);
            e.Property(a => a.AssetClass).HasMaxLength(24);
            e.Property(a => a.TokenStandard).HasMaxLength(16);
            e.Property(a => a.Summary).HasMaxLength(1000);
            e.Property(a => a.Highlights).HasMaxLength(2000);
            e.Property(a => a.ConstructionPhase).HasMaxLength(24);
            e.Property(a => a.PolygonContractAddress).HasMaxLength(42);
            e.Property(a => a.UnitPriceUsd).HasPrecision(18, 2);
            e.Property(a => a.BaseNavUsd).HasPrecision(18, 6);
            e.Property(a => a.CurrentNavUsd).HasPrecision(18, 6);
            e.Property(a => a.GoldWeightGramsPerUnit).HasPrecision(18, 6);
        });

        model.Entity<RwaHolding>(e =>
        {
            e.Property(h => h.TokenId).UseIdentityColumn();
            e.HasIndex(h => h.TokenId).IsUnique();
            e.Property(h => h.IntervalType).HasMaxLength(12);
            e.Property(h => h.Season).HasMaxLength(12);
            e.Property(h => h.SeasonWeight).HasPrecision(5, 2);
            e.Property(h => h.ShareFraction).HasPrecision(18, 10);
            e.Property(h => h.PrincipalUsd).HasPrecision(18, 2);
            e.Property(h => h.PaymentMethod).HasMaxLength(16);
            e.Property(h => h.Status).HasMaxLength(16);
            e.Property(h => h.VaultShares).HasPrecision(18, 6);
            e.Property(h => h.PolygonTxHash).HasMaxLength(66);
            e.HasIndex(h => new { h.UserId, h.RequestId }).IsUnique();
            e.HasIndex(h => new { h.Status, h.CoolingOffEndsAt });
            e.HasOne<User>().WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<RwaAsset>().WithMany().HasForeignKey(h => h.AssetId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<RwaWeekSlot>(e =>
        {
            e.HasKey(s => new { s.AssetId, s.Week });
            e.HasIndex(s => s.HoldingId);
            e.HasOne<RwaAsset>().WithMany().HasForeignKey(s => s.AssetId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<RwaHolding>().WithMany().HasForeignKey(s => s.HoldingId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RwaSimAccount>(e =>
        {
            e.HasKey(a => a.UserId);
            e.Property(a => a.BalanceUsd).HasPrecision(18, 2);
            e.HasOne<User>().WithOne().HasForeignKey<RwaSimAccount>(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<RwaAuditEvent>(e =>
        {
            e.Property(a => a.Kind).HasMaxLength(16);
            e.Property(a => a.Detail).HasMaxLength(1000);
            e.HasIndex(a => new { a.UserId, a.At });
        });
    }
}
