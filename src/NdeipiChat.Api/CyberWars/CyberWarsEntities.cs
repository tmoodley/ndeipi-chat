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
    public string Mode { get; set; } = MatchModes.Skirmish;

    /// <summary>The raided sector of <see cref="Province"/>; null for a skirmish.</summary>
    public int? SectorQ { get; set; }
    public int? SectorR { get; set; }

    public Guid? WinnerUserId { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }
    public Guid? WinnerAllianceId { get; set; }
}

public sealed class CyberWarsMatchPlayer
{
    public Guid MatchId { get; set; }
    public Guid UserId { get; set; }
    public string Side { get; set; } = MatchSides.Solo;
}

/// <summary>A player's home base on the world map (FR-2.1): one sector, which holds no other base.</summary>
public sealed class CyberWarsBase
{
    public Guid UserId { get; set; }
    public ZambianProvince Province { get; set; }
    public int SectorQ { get; set; }
    public int SectorR { get; set; }

    /// <summary>The base's structures relative to its Command Center (<see cref="BaseStructure"/>), as last saved.</summary>
    public string LayoutJson { get; set; } = "[]";

    public int Version { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Who holds a sector's Command Center after a raid captured it (FR-2.1a).</summary>
public sealed class CyberWarsSectorControl
{
    public ZambianProvince Province { get; set; }
    public int SectorQ { get; set; }
    public int SectorR { get; set; }
    public Guid ControllerUserId { get; set; }
    public Guid MatchId { get; set; }
    public DateTimeOffset CapturedAt { get; set; }
}

/// <summary>A Cooperative Society Alliance and its war room, a group chat in Ndeipi Chat (FR-2.2).</summary>
public sealed class CyberWarsAlliance
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public ZambianProvince Province { get; set; }
    public Guid ConversationId { get; set; }
    public Guid LeaderId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A player's alliance: one at a time.</summary>
public sealed class CyberWarsAllianceMember
{
    public Guid UserId { get; set; }
    public Guid AllianceId { get; set; }
    public DateTimeOffset JoinedAt { get; set; }

    /// <summary>The last /reinforce this member sent; one every few minutes.</summary>
    public DateTimeOffset? LastReinforceAt { get; set; }
}

/// <summary>An alliance's /vote-strike on a sector (FR-2.2b): one open at a time.</summary>
public sealed class CyberWarsStrike
{
    public Guid AllianceId { get; set; }
    public ZambianProvince Province { get; set; }
    public int SectorQ { get; set; }
    public int SectorR { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? ApprovedUntil { get; set; }

    /// <summary>The vote's card in the war room, updated in place as members vote.</summary>
    public Guid? CardMessageId { get; set; }
}

public sealed class CyberWarsStrikeBallot
{
    public Guid AllianceId { get; set; }
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
            e.Property(m => m.Mode).HasMaxLength(16);
            e.HasIndex(m => new { m.Status, m.StartedAt });
            e.HasIndex(m => new { m.Province, m.SectorQ, m.SectorR, m.Status });
        });

        model.Entity<CyberWarsMatchPlayer>(e =>
        {
            e.HasKey(p => new { p.MatchId, p.UserId });
            e.Property(p => p.Side).HasMaxLength(16);
            e.HasIndex(p => p.UserId);
            e.HasOne<CyberWarsMatch>().WithMany().HasForeignKey(p => p.MatchId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsBase>(e =>
        {
            e.HasKey(b => b.UserId);
            e.Property(b => b.Province).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(b => new { b.Province, b.SectorQ, b.SectorR }).IsUnique();
            e.HasOne<User>().WithOne().HasForeignKey<CyberWarsBase>(b => b.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsSectorControl>(e =>
        {
            e.HasKey(c => new { c.Province, c.SectorQ, c.SectorR });
            e.Property(c => c.Province).HasConversion<string>().HasMaxLength(16);
            e.HasOne<User>().WithMany().HasForeignKey(c => c.ControllerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsAlliance>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(40);
            e.Property(a => a.Province).HasConversion<string>().HasMaxLength(16);
            e.HasIndex(a => a.Name).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(a => a.ConversationId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<CyberWarsAllianceMember>(e =>
        {
            e.HasKey(m => m.UserId);
            e.HasIndex(m => m.AllianceId);
            e.HasOne<CyberWarsAlliance>().WithMany().HasForeignKey(m => m.AllianceId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithOne().HasForeignKey<CyberWarsAllianceMember>(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsStrike>(e =>
        {
            e.HasKey(s => s.AllianceId);
            e.Property(s => s.Province).HasConversion<string>().HasMaxLength(16);
            e.HasOne<CyberWarsAlliance>().WithOne().HasForeignKey<CyberWarsStrike>(s => s.AllianceId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<CyberWarsStrikeBallot>(e =>
        {
            e.HasKey(b => new { b.AllianceId, b.UserId });
            e.HasOne<CyberWarsStrike>().WithMany().HasForeignKey(b => b.AllianceId).OnDelete(DeleteBehavior.Cascade);
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
