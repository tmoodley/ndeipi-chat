using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;

namespace NdeipiChat.Api.Pos;

/// <summary>The POS tables' keys, indexes and column sizes.</summary>
public static class PosModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<PosMerchant>(e =>
        {
            e.Property(m => m.Name).HasMaxLength(120);
            e.Property(m => m.Currency).HasMaxLength(8);
            e.Property(m => m.DiscountLimitPercent).HasPrecision(5, 2);
            e.HasIndex(m => m.OwnerId);
        });

        model.Entity<PosStore>(e =>
        {
            e.Property(s => s.Name).HasMaxLength(120);
            e.Property(s => s.Address).HasMaxLength(300);
            e.Property(s => s.TaxRatePercent).HasPrecision(5, 2);
            e.HasIndex(s => s.MerchantId);
            e.HasOne<PosMerchant>().WithMany().HasForeignKey(s => s.MerchantId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PosStaff>(e =>
        {
            e.Property(s => s.Role).HasMaxLength(16);
            e.Property(s => s.PinHash).HasMaxLength(32);
            e.Property(s => s.PinSalt).HasMaxLength(16);
            e.HasIndex(s => new { s.MerchantId, s.UserId }).IsUnique();
            e.HasIndex(s => s.UserId);
            e.HasOne<PosMerchant>().WithMany().HasForeignKey(s => s.MerchantId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<PosTillSession>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(32);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.Property(t => t.Terminal).HasMaxLength(120);
            e.HasIndex(t => t.ExpiresAt);
        });

        model.Entity<PosCategory>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(60);
            e.Property(c => c.Icon).HasMaxLength(16);
            e.Property(c => c.Tone).HasMaxLength(16);
            e.HasIndex(c => c.MerchantId);
            e.HasOne<PosMerchant>().WithMany().HasForeignKey(c => c.MerchantId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PosProduct>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(120);
            e.Property(p => p.Icon).HasMaxLength(16);
            e.Property(p => p.Sku).HasMaxLength(64);
            e.Property(p => p.Barcode).HasMaxLength(64);
            e.Property(p => p.Price).HasPrecision(18, 2);
            e.Property(p => p.TaxRatePercent).HasPrecision(5, 2);
            e.HasIndex(p => new { p.MerchantId, p.Barcode });
            e.HasIndex(p => new { p.MerchantId, p.Sku });
            e.HasOne<PosMerchant>().WithMany().HasForeignKey(p => p.MerchantId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PosStock>(e =>
        {
            e.HasKey(s => new { s.StoreId, s.ProductId, s.Variant });
            e.Property(s => s.Variant).HasMaxLength(60);
            e.Property(s => s.Quantity).HasPrecision(18, 3);
            e.HasOne<PosStore>().WithMany().HasForeignKey(s => s.StoreId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<PosProduct>().WithMany().HasForeignKey(s => s.ProductId).OnDelete(DeleteBehavior.NoAction);
        });

        model.Entity<PosShift>(e =>
        {
            e.Property(s => s.OpeningFloat).HasPrecision(18, 2);
            e.Property(s => s.CountedCash).HasPrecision(18, 2);
            e.HasIndex(s => new { s.StoreId, s.OpenedAt });
            e.HasIndex(s => new { s.StaffId, s.ClosedAt });
        });

        model.Entity<PosCashMovement>(e =>
        {
            e.Property(m => m.Kind).HasMaxLength(16);
            e.Property(m => m.Amount).HasPrecision(18, 2);
            e.Property(m => m.Note).HasMaxLength(200);
            e.HasIndex(m => m.ShiftId);
        });

        model.Entity<PosSale>(e =>
        {
            e.Property(s => s.Status).HasMaxLength(16);
            e.Property(s => s.StatusReason).HasMaxLength(300);
            foreach (var money in new[] { nameof(PosSale.Subtotal), nameof(PosSale.Discount), nameof(PosSale.Tax), nameof(PosSale.Total), nameof(PosSale.Change) })
                e.Property(money).HasPrecision(18, 2);
            // FR-PAY-02: a sale's idempotency key is unique within its merchant.
            e.HasIndex(s => new { s.MerchantId, s.ClientSaleId }).IsUnique();
            e.HasIndex(s => new { s.StoreId, s.OccurredAt });
            e.HasIndex(s => s.ShiftId);
            e.HasMany(s => s.Lines).WithOne().HasForeignKey(l => l.SaleId).OnDelete(DeleteBehavior.Cascade);
            e.HasMany(s => s.Payments).WithOne().HasForeignKey(p => p.SaleId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<PosSaleLine>(e =>
        {
            e.Property(l => l.Name).HasMaxLength(120);
            e.Property(l => l.Variant).HasMaxLength(60);
            e.Property(l => l.Modifiers).HasMaxLength(500);
            e.Property(l => l.Quantity).HasPrecision(18, 3);
            foreach (var money in new[] { nameof(PosSaleLine.UnitPrice), nameof(PosSaleLine.Discount), nameof(PosSaleLine.Tax), nameof(PosSaleLine.Total) })
                e.Property(money).HasPrecision(18, 2);
            e.Property(l => l.TaxRatePercent).HasPrecision(5, 2);
        });

        model.Entity<PosPayment>(e =>
        {
            e.Property(p => p.Tender).HasMaxLength(16);
            e.Property(p => p.Amount).HasPrecision(18, 2);
            e.Property(p => p.Reference).HasMaxLength(64);
        });

        model.Entity<PosAuditEntry>(e =>
        {
            e.Property(a => a.StaffName).HasMaxLength(120);
            e.Property(a => a.Terminal).HasMaxLength(120);
            e.Property(a => a.Action).HasMaxLength(32);
            e.Property(a => a.Details).HasMaxLength(2000);
            e.Property(a => a.PreviousHash).HasMaxLength(64);
            e.Property(a => a.Hash).HasMaxLength(64);
            // One chain per merchant; a sequence number can't be taken twice.
            e.HasIndex(a => new { a.MerchantId, a.Sequence }).IsUnique();
        });
    }
}
