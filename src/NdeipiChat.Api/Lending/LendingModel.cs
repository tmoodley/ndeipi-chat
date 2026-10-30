using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Rwa;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Lending;

public static class LendingModel
{
    public const string AuditTrigger = "TR_LendingAuditEvents_AppendOnly";

    public static void Configure(ModelBuilder model)
    {
        model.Entity<LendingDeal>(e =>
        {
            e.Property(d => d.Code).HasMaxLength(32);
            e.HasIndex(d => d.Code).IsUnique();
            e.Property(d => d.Name).HasMaxLength(LendingContract.MaxNameLength);
            e.Property(d => d.Location).HasMaxLength(LendingContract.MaxNameLength);
            e.Property(d => d.State).HasMaxLength(16);
            e.Property(d => d.OfferingBasis).HasMaxLength(24);
            e.Property(d => d.ReturnReason).HasMaxLength(LendingContract.MaxReasonLength);
            e.Property(d => d.BlockedJurisdictions).HasMaxLength(200);
            foreach (var money in new[]
            {
                nameof(LendingDeal.SeniorTargetUsd), nameof(LendingDeal.JuniorTargetUsd), nameof(LendingDeal.ReserveTargetUsd), nameof(LendingDeal.ScheduledSeniorPrincipalUsd),
                nameof(LendingDeal.MaxHoldingUsd), nameof(LendingDeal.SeniorFundedUsd), nameof(LendingDeal.JuniorFundedUsd), nameof(LendingDeal.SeniorOutUsd), nameof(LendingDeal.JuniorOutUsd),
                nameof(LendingDeal.SeniorInterestPaidUsd), nameof(LendingDeal.JuniorInterestPaidUsd), nameof(LendingDeal.SeniorPrincipalPaidUsd), nameof(LendingDeal.JuniorPrincipalPaidUsd),
                nameof(LendingDeal.SeniorLossUsd), nameof(LendingDeal.JuniorLossUsd), nameof(LendingDeal.ReserveUsd), nameof(LendingDeal.ValuationUsd), nameof(LendingDeal.SeniorRedeemedUsd), nameof(LendingDeal.JuniorRedeemedUsd)
            })
                e.Property(money).HasPrecision(18, 2);
            // Interest builds up by the second, so what's due keeps more places than whole cents.
            e.Property(d => d.SeniorDueUsd).HasPrecision(24, 10);
            e.Property(d => d.JuniorDueUsd).HasPrecision(24, 10);
            e.Property(d => d.FeesDueUsd).HasPrecision(24, 10);
            e.HasIndex(d => d.State);
            e.HasOne<RwaAsset>().WithMany().HasForeignKey(d => d.RwaAssetId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LendingDraw>(e =>
        {
            e.HasKey(d => new { d.DealId, d.Number });
            e.Property(d => d.Milestone).HasMaxLength(LendingContract.MaxNameLength);
            e.Property(d => d.AmountUsd).HasPrecision(18, 2);
            e.Property(d => d.BrokerFeeUsd).HasPrecision(18, 2);
            e.Property(d => d.PlatformFeeUsd).HasPrecision(18, 2);
            e.Property(d => d.NetUsd).HasPrecision(18, 2);
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(d => d.DealId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LendingDealRole>(e =>
        {
            e.HasKey(r => new { r.DealId, r.Role });
            e.Property(r => r.Role).HasMaxLength(16);
            e.HasIndex(r => r.UserId);
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(r => r.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LendingPosition>(e =>
        {
            e.HasKey(p => new { p.DealId, p.UserId, p.Tranche });
            e.Property(p => p.Tranche).HasMaxLength(8);
            e.Property(p => p.DepositedUsd).HasPrecision(18, 2);
            e.Property(p => p.RedeemedUsd).HasPrecision(18, 2);
            e.Property(p => p.InterestPaidUsd).HasPrecision(18, 2);
            e.Property(p => p.PrincipalPaidUsd).HasPrecision(18, 2);
            e.Property(p => p.LossUsd).HasPrecision(18, 2);
            e.HasIndex(p => p.UserId);
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(p => p.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LendingDeposit>(e =>
        {
            e.HasKey(d => new { d.UserId, d.RequestId });
            e.Property(d => d.Tranche).HasMaxLength(8);
            e.Property(d => d.AmountUsd).HasPrecision(18, 2);
            e.HasOne<User>().WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LendingDocument>(e =>
        {
            e.Property(d => d.Name).HasMaxLength(LendingContract.MaxNameLength);
            e.Property(d => d.Kind).HasMaxLength(24);
            e.Property(d => d.Sha256).HasMaxLength(64);
            e.HasIndex(d => new { d.DealId, d.Name, d.Version }).IsUnique();
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(d => d.DealId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LendingAttestation>(e =>
        {
            e.Property(a => a.Kind).HasMaxLength(16);
            e.Property(a => a.ReportSha256).HasMaxLength(64);
            e.Property(a => a.ValueUsd).HasPrecision(18, 2);
            e.HasIndex(a => new { a.DealId, a.Kind });
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(a => a.DealId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LendingPayment>(e =>
        {
            e.Property(p => p.Reference).HasMaxLength(64);
            e.HasIndex(p => new { p.DealId, p.Reference }).IsUnique();
            foreach (var money in new[]
            {
                nameof(LendingPayment.AmountUsd), nameof(LendingPayment.Fees), nameof(LendingPayment.SeniorInterest), nameof(LendingPayment.SeniorPrincipal), nameof(LendingPayment.JuniorInterest),
                nameof(LendingPayment.SeniorPrincipalSweep), nameof(LendingPayment.JuniorPrincipal), nameof(LendingPayment.Reserve), nameof(LendingPayment.Residual)
            })
                e.Property(money).HasPrecision(18, 2);
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(p => p.DealId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LendingDefaultVote>(e =>
        {
            e.HasKey(v => new { v.DealId, v.Role });
            e.Property(v => v.Role).HasMaxLength(16);
            e.Property(v => v.Reason).HasMaxLength(LendingContract.MaxReasonLength);
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(v => v.DealId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LendingProfile>(e =>
        {
            e.HasKey(p => p.UserId);
            e.Property(p => p.InvestorClass).HasMaxLength(20);
            e.Property(p => p.Jurisdiction).HasMaxLength(2);
            e.Property(p => p.Reason).HasMaxLength(LendingContract.MaxReasonLength);
            e.HasOne<User>().WithOne().HasForeignKey<LendingProfile>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LendingRedemption>(e =>
        {
            e.Property(r => r.Tranche).HasMaxLength(8);
            e.Property(r => r.Status).HasMaxLength(12);
            e.Property(r => r.AmountUsd).HasPrecision(18, 2);
            e.HasIndex(r => new { r.DealId, r.UserId });
            e.HasOne<LendingDeal>().WithMany().HasForeignKey(r => r.DealId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(r => r.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LendingNotice>(e =>
        {
            e.Property(n => n.Text).HasMaxLength(300);
            e.HasIndex(n => new { n.UserId, n.At });
        });

        model.Entity<LendingAuditEvent>(e =>
        {
            e.ToTable(t => t.HasTrigger(AuditTrigger));
            e.Property(a => a.Action).HasMaxLength(32);
            e.Property(a => a.Before).HasMaxLength(200);
            e.Property(a => a.After).HasMaxLength(200);
            e.Property(a => a.Reason).HasMaxLength(LendingContract.MaxReasonLength);
            e.HasIndex(a => new { a.DealId, a.At });
        });
    }
}
