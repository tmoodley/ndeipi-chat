using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Market;

public static class MarketModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<ChatMedia>(e =>
        {
            e.Property(m => m.Type).HasMaxLength(8);
            e.Property(m => m.ContentType).HasMaxLength(40);
            e.HasIndex(m => new { m.ConversationId, m.CreatedAt });
            e.HasIndex(m => m.UploaderId);
        });

        model.Entity<MarketListing>(e =>
        {
            e.Property(l => l.Title).HasMaxLength(MarketContract.MaxTitleLength);
            e.Property(l => l.Species).HasMaxLength(16);
            e.Property(l => l.Breed).HasMaxLength(60);
            e.Property(l => l.Price).HasPrecision(18, 2);
            e.Property(l => l.Currency).HasMaxLength(3);
            e.Property(l => l.Location).HasMaxLength(80);
            e.Property(l => l.Age).HasMaxLength(40);
            e.Property(l => l.Description).HasMaxLength(MarketContract.MaxDescriptionLength);
            e.Property(l => l.BarterTerms).HasMaxLength(200);
            e.Property(l => l.Status).HasMaxLength(16);
            e.Property(l => l.MediaIds).HasMaxLength(400);
            e.Property(l => l.Tags).HasMaxLength(600);
            e.HasIndex(l => new { l.SellerId, l.UpdatedAt });
            e.HasIndex(l => new { l.Status, l.UpdatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(l => l.SellerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<MarketListingPost>(e =>
        {
            e.HasKey(p => p.MessageId);
            e.HasIndex(p => new { p.ListingId, p.ConversationId });
            e.HasIndex(p => new { p.ConversationId, p.PostedAt });
            e.HasOne<MarketListing>().WithMany().HasForeignKey(p => p.ListingId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<MarketOffer>(e =>
        {
            e.Property(o => o.Kind).HasMaxLength(8);
            e.Property(o => o.Amount).HasPrecision(18, 2);
            e.Property(o => o.Currency).HasMaxLength(3);
            e.Property(o => o.Text).HasMaxLength(MarketContract.MaxOfferLength);
            e.Property(o => o.Status).HasMaxLength(12);
            e.HasIndex(o => new { o.ListingId, o.Status });
            e.HasIndex(o => new { o.BuyerId, o.CreatedAt });
            e.HasIndex(o => o.MessageId).IsUnique();
            e.HasOne<MarketListing>().WithMany().HasForeignKey(o => o.ListingId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(o => o.BuyerId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
