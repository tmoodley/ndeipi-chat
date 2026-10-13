using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Travel;

public static class TravelModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<TravelVendor>(e =>
        {
            e.HasKey(v => v.UserId);
            e.Property(v => v.Name).HasMaxLength(TravelContract.MaxNameLength);
            e.Property(v => v.Description).HasMaxLength(1000);
            e.Property(v => v.Phone).HasMaxLength(40);
            e.Property(v => v.Email).HasMaxLength(200);
            e.Property(v => v.Status).HasMaxLength(20);
            e.HasOne<User>().WithOne().HasForeignKey<TravelVendor>(v => v.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TravelProperty>(e =>
        {
            e.Property(p => p.Name).HasMaxLength(TravelContract.MaxNameLength);
            e.Property(p => p.Type).HasMaxLength(20);
            e.Property(p => p.Description).HasMaxLength(TravelContract.MaxDescriptionLength);
            e.Property(p => p.City).HasMaxLength(80);
            e.Property(p => p.Country).HasMaxLength(80);
            e.Property(p => p.Address).HasMaxLength(300);
            e.Property(p => p.Amenities).HasMaxLength(400);
            e.Property(p => p.Cancellation).HasMaxLength(20);
            e.Property(p => p.CheckInTime).HasMaxLength(5);
            e.Property(p => p.CheckOutTime).HasMaxLength(5);
            e.Property(p => p.Status).HasMaxLength(20);
            e.Property(p => p.PhotoIds).HasMaxLength(400);
            e.HasIndex(p => new { p.Status, p.City });
            e.HasIndex(p => p.VendorId);
        });

        model.Entity<RoomType>(e =>
        {
            e.Property(r => r.Name).HasMaxLength(80);
            e.Property(r => r.Kind).HasMaxLength(20);
            e.Property(r => r.BoardPlan).HasMaxLength(20);
            e.Property(r => r.NightlyPrice).HasPrecision(18, 2);
            e.Property(r => r.Description).HasMaxLength(1000);
            e.Property(r => r.RowVersion).IsRowVersion();
            e.HasIndex(r => r.PropertyId);
        });

        model.Entity<RoomNight>(e =>
        {
            e.HasKey(n => new { n.RoomTypeId, n.Date });
            e.Property(n => n.Price).HasPrecision(18, 2);
            e.ToTable(t => t.HasCheckConstraint("CK_RoomNights_Counts", "[Held] >= 0 AND [Booked] >= 0"));
        });

        model.Entity<TourPackage>(e =>
        {
            e.Property(t => t.Title).HasMaxLength(TravelContract.MaxNameLength);
            e.Property(t => t.Kind).HasMaxLength(30);
            e.Property(t => t.Description).HasMaxLength(TravelContract.MaxDescriptionLength);
            e.Property(t => t.City).HasMaxLength(80);
            e.Property(t => t.Country).HasMaxLength(80);
            e.Property(t => t.Difficulty).HasMaxLength(20);
            e.Property(t => t.PricePerPerson).HasPrecision(18, 2);
            e.Property(t => t.Includes).HasMaxLength(1000);
            e.Property(t => t.MeetingPoint).HasMaxLength(300);
            e.Property(t => t.Status).HasMaxLength(20);
            e.Property(t => t.PhotoIds).HasMaxLength(400);
            e.HasIndex(t => new { t.Status, t.City });
            e.HasIndex(t => t.VendorId);
        });

        model.Entity<TourAddOn>(e =>
        {
            e.Property(a => a.Name).HasMaxLength(80);
            e.Property(a => a.Price).HasPrecision(18, 2);
            e.HasIndex(a => a.TourId);
        });

        model.Entity<TourDeparture>(e =>
        {
            e.Property(d => d.Guide).HasMaxLength(120);
            e.Property(d => d.Status).HasMaxLength(20);
            e.Property(d => d.Note).HasMaxLength(500);
            e.Property(d => d.RowVersion).IsRowVersion();
            e.HasIndex(d => new { d.TourId, d.StartsAt });
            e.ToTable(t => t.HasCheckConstraint("CK_TourDepartures_Counts", "[Held] >= 0 AND [Booked] >= 0"));
        });

        model.Entity<TravelHold>(e =>
        {
            e.Property(h => h.ItemType).HasMaxLength(10);
            e.Property(h => h.LinesJson).HasMaxLength(4000);
            e.Property(h => h.Amount).HasPrecision(18, 2);
            e.Property(h => h.Status).HasMaxLength(20);
            e.HasIndex(h => new { h.Status, h.ExpiresAt });
            e.HasIndex(h => h.UserId);
        });

        model.Entity<TravelBooking>(e =>
        {
            e.Property(b => b.Reference).HasMaxLength(12);
            e.HasIndex(b => b.Reference).IsUnique();
            e.HasIndex(b => b.HoldId).IsUnique();
            e.Property(b => b.Status).HasMaxLength(20);
            e.Property(b => b.Amount).HasPrecision(18, 2);
            e.Property(b => b.Fee).HasPrecision(18, 2);
            e.Property(b => b.Discount).HasPrecision(18, 2);
            e.Property(b => b.Currency).HasMaxLength(10);
            e.Property(b => b.LeadGuestName).HasMaxLength(TravelContract.MaxNameLength);
            e.Property(b => b.Phone).HasMaxLength(40);
            e.Property(b => b.IdNumberProtected).HasMaxLength(1000);
            e.Property(b => b.SpecialRequests).HasMaxLength(TravelContract.MaxRequestsLength);
            e.Property(b => b.Error).HasMaxLength(500);
            e.Property(b => b.Notice).HasMaxLength(500);
            e.Property(b => b.HolderPublicKey).HasMaxLength(200);
            e.Property(b => b.InvoiceNumber).HasMaxLength(30);
            e.Property(b => b.CheckedInBy).HasMaxLength(200);
            e.HasIndex(b => new { b.TravelerId, b.CreatedAt });
            e.HasIndex(b => new { b.VendorId, b.CreatedAt });
            e.HasIndex(b => b.BankTransferId);
        });

        model.Entity<TravelBookingItem>(e =>
        {
            e.Property(i => i.ItemType).HasMaxLength(10);
            e.Property(i => i.Title).HasMaxLength(200);
            e.Property(i => i.Subtitle).HasMaxLength(200);
            e.Property(i => i.LinesJson).HasMaxLength(4000);
            e.Property(i => i.Amount).HasPrecision(18, 2);
            e.HasIndex(i => i.BookingId);
            e.HasIndex(i => new { i.ItemType, i.ReferenceId });
        });

        model.Entity<TravelPhoto>(e => e.HasIndex(p => p.VendorId));

        model.Entity<TravelSigningKey>(e =>
        {
            e.Property(k => k.Id).HasMaxLength(40);
            e.Property(k => k.PublicKey).HasMaxLength(200);
            e.Property(k => k.PrivateKeyProtected).HasMaxLength(2000);
        });
    }
}
