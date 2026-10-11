using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ndeipi.Payments.Data;

/// <summary>The business-banking tables. Ndeipi's own: no integrator owns them, so no query filter applies.</summary>
static class BankingModel
{
    public static void Configure(ModelBuilder model, ValueConverter identity)
    {
        model.Entity<TreasuryAccount>(e =>
        {
            e.Property(a => a.Id).HasMaxLength(256);
            e.Property(a => a.Name).HasMaxLength(200);
            e.Property(a => a.Type).HasMaxLength(40);
            e.Property(a => a.Last4).HasMaxLength(4);
            e.Property(a => a.Currency).HasMaxLength(3);
            e.Property(a => a.Status).HasMaxLength(40);
            e.Property(a => a.Bank).HasMaxLength(200);
            e.Property(a => a.Available).HasPrecision(38, 2);
            e.Property(a => a.Current).HasPrecision(38, 2);
        });

        model.Entity<Payee>(e =>
        {
            e.Property(p => p.Id).HasMaxLength(40);
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
            // Who Ndeipi pays, and where, is encrypted at rest like integrators' users' details.
            e.Property(p => p.Name).HasMaxLength(1000).HasConversion(identity);
            e.Property(p => p.Address).HasMaxLength(1000).HasConversion(identity);
            e.Property(p => p.Display).HasMaxLength(120);
            e.Property(p => p.Country).HasMaxLength(2);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.ProviderContactId).HasMaxLength(100);
            e.Property(p => p.ProviderPayeeId).HasMaxLength(100);
            e.Property(p => p.CreatedBy).HasMaxLength(300);
        });

        model.Entity<TreasuryPayment>(e =>
        {
            e.Property(p => p.Id).HasMaxLength(40);
            e.Property(p => p.Rail).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.AccountId).HasMaxLength(256);
            e.Property(p => p.PayeeId).HasMaxLength(40);
            e.Property(p => p.Amount).HasPrecision(38, 2);
            e.Property(p => p.RecipientCurrency).HasMaxLength(3);
            e.Property(p => p.Purpose).HasMaxLength(140);
            e.Property(p => p.Memo).HasMaxLength(255);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(p => p.RequestKey).HasMaxLength(255);
            e.HasIndex(p => p.RequestKey).IsUnique();
            e.Property(p => p.InitiatorType).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.InitiatedBy).HasMaxLength(300);
            e.Property(p => p.InitiatedById).HasMaxLength(200);
            e.Property(p => p.AgentSessionId).HasMaxLength(40);
            e.Property(p => p.AgentModel).HasMaxLength(100);
            e.Property(p => p.ProviderApprovalId).HasMaxLength(100);
            e.Property(p => p.ProviderTransferId).HasMaxLength(200);
            e.HasIndex(p => p.ProviderTransferId);
            e.Property(p => p.ProviderStatus).HasMaxLength(40);
            e.Property(p => p.FailureReason).HasMaxLength(500);
            e.Property(p => p.Version).IsConcurrencyToken();
            e.HasIndex(p => new { p.Status, p.NextDispatchAt });
            e.HasIndex(p => p.CreatedAt);
        });

        model.Entity<PaymentApproval>(e =>
        {
            e.Property(a => a.PaymentId).HasMaxLength(40);
            e.Property(a => a.OfficerId).HasMaxLength(200);
            e.Property(a => a.Officer).HasMaxLength(300);
            e.Property(a => a.Decision).HasConversion<string>().HasMaxLength(10);
            e.Property(a => a.TermsDigest).HasMaxLength(64);
            e.Property(a => a.Note).HasMaxLength(500);
            // One decision per officer per payment, however the requests race.
            e.HasIndex(a => new { a.PaymentId, a.OfficerId }).IsUnique();
        });

        model.Entity<CorporateCard>(e =>
        {
            e.Property(c => c.Id).HasMaxLength(40);
            e.Property(c => c.ProviderCardId).HasMaxLength(100);
            e.HasIndex(c => c.ProviderCardId).IsUnique();
            e.Property(c => c.Nickname).HasMaxLength(30);
            e.Property(c => c.Last4).HasMaxLength(4);
            e.Property(c => c.Cardholder).HasMaxLength(200);
            e.Property(c => c.DebitAccountId).HasMaxLength(256);
            e.Property(c => c.AllowedCategories).HasMaxLength(1500);
            e.Property(c => c.State).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Purpose).HasMaxLength(500);
            e.Property(c => c.CreatedBy).HasMaxLength(300);
            e.Property(c => c.Version).IsConcurrencyToken();
        });

        model.Entity<BankWebhookEvent>(e =>
        {
            e.Property(v => v.EventId).HasMaxLength(200);
            e.HasIndex(v => v.EventId).IsUnique();
            e.Property(v => v.EventType).HasMaxLength(100);
            e.Property(v => v.Signature).HasMaxLength(2000);
            e.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(v => v.Error).HasMaxLength(500);
            e.HasIndex(v => new { v.Status, v.Id });
        });

        model.Entity<BankingJournalEntry>(e =>
        {
            e.ToTable("BankingJournal");
            e.Property(j => j.ActorType).HasConversion<string>().HasMaxLength(20);
            e.Property(j => j.Actor).HasMaxLength(300);
            e.Property(j => j.Action).HasMaxLength(60);
            e.Property(j => j.Subject).HasMaxLength(256);
            e.Property(j => j.Detail).HasMaxLength(1000);
            e.Property(j => j.AgentSessionId).HasMaxLength(40);
            e.Property(j => j.AgentModel).HasMaxLength(100);
            e.HasIndex(j => j.Subject);
        });

        model.Entity<AssistantSession>(e =>
        {
            e.Property(s => s.Id).HasMaxLength(40);
            e.Property(s => s.OfficerId).HasMaxLength(200);
            e.Property(s => s.Officer).HasMaxLength(300);
        });

        model.Entity<AssistantMessage>(e =>
        {
            e.Property(m => m.SessionId).HasMaxLength(40);
            e.Property(m => m.Role).HasMaxLength(10);
            e.HasIndex(m => new { m.SessionId, m.Id });
        });
    }
}
