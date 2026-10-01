using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Finance;

public static class FinanceModel
{
    /// <summary>
    /// The processing equipment on the application screens, all mercury-free. Prices are left to be
    /// confirmed: an admin sets them in the app.
    /// </summary>
    public static readonly LoanEquipment[] Equipment =
    [
        new() { Id = Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a01"), Name = "Jaw crusher", Description = "Primary crushing of hard rock ore", ClusterType = ClusterTypes.Processing, Mtp = "MTP 1", Order = 1 },
        new() { Id = Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a02"), Name = "Hammer mill", Description = "Milling crushed ore to fine particles", ClusterType = ClusterTypes.Processing, Mtp = "MTP 2", Order = 2 },
        new() { Id = Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a03"), Name = "Gold kacha", Description = "Centrifugal concentrator for coarse and fine gold; 1–3 t/h, 2–4 m³ water/h", ClusterType = ClusterTypes.Processing, Mtp = "MTP (TBC)", Order = 3 },
        new() { Id = Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a04"), Name = "Shaking table", Description = "Final clean-up to smeltable concentrate", ClusterType = ClusterTypes.Processing, Mtp = "MTP (TBC)", Order = 4 },
        new() { Id = Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a05"), Name = "Sluice box", Description = "Portable recovery from gravel and river sand", ClusterType = ClusterTypes.Processing, Mtp = "MTP (TBC)", Order = 5 }
    ];

    public static void Configure(ModelBuilder model)
    {
        model.Entity<LoanApplication>(e =>
        {
            e.Property(a => a.Reference).HasMaxLength(16);
            e.HasIndex(a => a.Reference).IsUnique();
            e.Property(a => a.Status).HasMaxLength(16);
            e.Property(a => a.Stage).HasMaxLength(16);
            e.Property(a => a.RegistrationBody).HasMaxLength(24);
            e.Property(a => a.LicenceType).HasMaxLength(24);
            e.Property(a => a.GroupName).HasMaxLength(FinanceContract.MaxNameLength);
            e.Property(a => a.GroupType).HasMaxLength(24);
            e.Property(a => a.RegistrationNumber).HasMaxLength(60);
            e.Property(a => a.Province).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(a => a.Constituency).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(a => a.Ward).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(a => a.Village).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(a => a.ClusterType).HasMaxLength(16);
            e.Property(a => a.EstimateTotal).HasPrecision(18, 2);
            e.Property(a => a.CollectionReference).HasMaxLength(60);
            e.HasIndex(a => new { a.ApplicantId, a.Status });
            e.HasIndex(a => new { a.Status, a.Stage });
            e.HasIndex(a => a.RegistrationNumber);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.ApplicantId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LoanApplicationItem>(e =>
        {
            e.HasKey(i => new { i.ApplicationId, i.EquipmentId });
            e.Property(i => i.Name).HasMaxLength(FinanceContract.MaxNameLength);
            e.Property(i => i.HirePerMonth).HasPrecision(18, 2);
            e.Property(i => i.BuyPrice).HasPrecision(18, 2);
            e.Property(i => i.RunningPerMonth).HasPrecision(18, 2);
            e.HasOne<LoanApplication>().WithMany(a => a.Items).HasForeignKey(i => i.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LoanDocument>(e =>
        {
            e.Property(d => d.Kind).HasMaxLength(16);
            e.Property(d => d.FileName).HasMaxLength(200);
            e.Property(d => d.ContentType).HasMaxLength(60);
            e.Property(d => d.StoredAs).HasMaxLength(200);
            e.HasIndex(d => new { d.ApplicationId, d.Kind }).IsUnique();
            e.HasOne<LoanApplication>().WithMany(a => a.Documents).HasForeignKey(d => d.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LoanDecision>(e =>
        {
            e.Property(d => d.Stage).HasMaxLength(16);
            e.Property(d => d.Decision).HasMaxLength(16);
            e.Property(d => d.Comment).HasMaxLength(FinanceContract.MaxCommentLength);
            e.HasIndex(d => new { d.ApplicationId, d.CreatedAt });
            e.HasOne<LoanApplication>().WithMany(a => a.Decisions).HasForeignKey(d => d.ApplicationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(d => d.DeciderId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<LoanCommittee>(e =>
        {
            e.Property(c => c.Stage).HasMaxLength(16);
            e.Property(c => c.Name).HasMaxLength(FinanceContract.MaxNameLength);
            e.Property(c => c.Province).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(c => c.Constituency).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(c => c.Ward).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(c => c.Village).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.HasIndex(c => new { c.Stage, c.Province });
        });

        model.Entity<LoanCommitteeMember>(e =>
        {
            e.HasKey(m => new { m.CommitteeId, m.UserId });
            e.HasIndex(m => m.UserId);
            e.HasOne<LoanCommittee>().WithMany(c => c.Members).HasForeignKey(m => m.CommitteeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<LoanEquipment>(e =>
        {
            e.Property(q => q.Name).HasMaxLength(FinanceContract.MaxNameLength);
            e.Property(q => q.Description).HasMaxLength(300);
            e.Property(q => q.ClusterType).HasMaxLength(16);
            e.Property(q => q.Mtp).HasMaxLength(24);
            e.Property(q => q.HirePerMonth).HasPrecision(18, 2);
            e.Property(q => q.BuyPrice).HasPrecision(18, 2);
            e.Property(q => q.RunningPerMonth).HasPrecision(18, 2);
            e.HasData(Equipment);
        });

        model.Entity<FinanceConstituency>(e =>
        {
            e.Property(c => c.Province).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.Property(c => c.Name).HasMaxLength(FinanceContract.MaxPlaceLength);
            e.HasIndex(c => new { c.Province, c.Name }).IsUnique();
        });
    }
}
