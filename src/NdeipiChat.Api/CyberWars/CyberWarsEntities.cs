using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>One row per player. Rank stays None until rank packs are sold (FR-1.1).</summary>
public sealed class CyberWarsPlayer
{
    public Guid UserId { get; set; }
    public PlayerRank Rank { get; set; }
    public long GnTokens { get; set; }
    public ZambianProvince? HomeProvince { get; set; }
    public DateTimeOffset? ProtectedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class CyberWarsMatch
{
    public Guid Id { get; set; }
    public ZambianProvince Province { get; set; }

    /// <summary>The day the veins' richness was set from (FR-1.3a).</summary>
    public DateOnly Day { get; set; }

    public string Status { get; set; } = MatchStatuses.Running;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? WinnerAllianceId { get; set; }
}

public sealed class CyberWarsMatchPlayer
{
    public Guid MatchId { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>A match's latest state (NFR-3a); one row per match, overwritten every 10 s.</summary>
public sealed class CyberWarsSnapshot
{
    public Guid MatchId { get; set; }
    public long Tick { get; set; }
    public string StateBlob { get; set; } = "";
    public DateTimeOffset At { get; set; }
}

/// <summary>Every order and its answer (NFR-2a), by user id only (NFR-7).</summary>
public sealed class CyberWarsOrderLog
{
    public Guid OrderId { get; set; }
    public Guid MatchId { get; set; }
    public Guid UserId { get; set; }
    public OrderKind Kind { get; set; }
    public long Tick { get; set; }
    public bool Accepted { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; }
}

public static class CyberWarsModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<CyberWarsPlayer>(e =>
        {
            e.HasKey(p => p.UserId);
            e.Property(p => p.Rank).HasConversion<string>().HasMaxLength(16);
            e.Property(p => p.HomeProvince).HasConversion<string>().HasMaxLength(16);
            e.HasOne<User>().WithOne().HasForeignKey<CyberWarsPlayer>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsMatch>(e =>
        {
            e.Property(m => m.Province).HasConversion<string>().HasMaxLength(16);
            e.Property(m => m.Status).HasMaxLength(16);
            e.HasIndex(m => new { m.Status, m.StartedAt });
        });

        model.Entity<CyberWarsMatchPlayer>(e =>
        {
            e.HasKey(p => new { p.MatchId, p.UserId });
            e.HasIndex(p => p.UserId);
            e.HasOne<CyberWarsMatch>().WithMany().HasForeignKey(p => p.MatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsSnapshot>(e =>
        {
            e.HasKey(s => s.MatchId);
            e.HasOne<CyberWarsMatch>().WithOne().HasForeignKey<CyberWarsSnapshot>(s => s.MatchId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsOrderLog>(e =>
        {
            e.HasKey(o => o.OrderId);
            e.Property(o => o.Kind).HasConversion<string>().HasMaxLength(16);
            e.Property(o => o.Reason).HasMaxLength(200);
            e.HasIndex(o => new { o.MatchId, o.UserId, o.At });
            e.HasIndex(o => o.At);
        });
    }
}
