using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;

namespace NdeipiChat.Api.Trust;

public static class TrustModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<TrustLink>(e =>
        {
            e.Property(l => l.Platform).HasMaxLength(16);
            e.Property(l => l.ExternalId).HasMaxLength(100);
            e.Property(l => l.Handle).HasMaxLength(120);
            e.Property(l => l.AccessTokenProtected).HasMaxLength(4000);
            e.Property(l => l.RefreshTokenProtected).HasMaxLength(4000);
            // One outside account backs one Ndeipi account, and each person links a platform once.
            e.HasIndex(l => new { l.Platform, l.ExternalId }).IsUnique();
            e.HasIndex(l => new { l.UserId, l.Platform }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TrustScoreRecord>(e =>
        {
            e.HasKey(s => s.UserId);
            e.Property(s => s.Tier1).HasPrecision(6, 2);
            e.Property(s => s.Tier2).HasPrecision(6, 2);
            e.Property(s => s.Tier3).HasPrecision(6, 2);
            e.Property(s => s.HistoryPoints).HasPrecision(6, 2);
            e.Property(s => s.WorkPoints).HasPrecision(6, 2);
            e.Property(s => s.Platforms).HasMaxLength(200);
            e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
