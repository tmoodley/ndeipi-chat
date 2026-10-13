using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ndeipi.Payments.Data;

/// <summary>
/// Whose data this request may touch: set from the API key on every API request (SC-05).
/// Background work that spans integrators (dispatch, reconciliation) leaves it empty and opts out
/// of the filters explicitly with <c>IgnoreQueryFilters()</c>.
/// </summary>
public sealed class IntegratorScope
{
    /// <summary>
    /// The owner of Ndeipi's own ledger accounts (floats, reserve, treasury, NdeipiCoin stock, fees).
    /// No API key ever carries it, so no integrator can read those accounts.
    /// </summary>
    public static readonly Guid House = new("00000000-0000-0000-0000-00000000000a");

    public Guid? IntegratorId { get; private set; }
    public Guid? ApiKeyId { get; private set; }

    public void Set(Guid integratorId, Guid? apiKeyId)
    {
        if (IntegratorId is not null && IntegratorId != integratorId)
            throw new InvalidOperationException("A request's integrator cannot change.");
        IntegratorId = integratorId;
        ApiKeyId = apiKeyId;
    }
}

public sealed class PaymentsDbContext(
    DbContextOptions<PaymentsDbContext> options, IntegratorScope scope, IDataProtectionProvider protection) : DbContext(options), IDataProtectionKeyContext
{
    public const string Schema = "payments";

    // Read by the query filters; EF evaluates it per query, so one model serves every integrator.
    Guid? CurrentIntegratorId => scope.IntegratorId;

    public DbSet<Integrator> Integrators => Set<Integrator>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<AuditEntry> AuditLog => Set<AuditEntry>();
    public DbSet<PaymentUser> Users => Set<PaymentUser>();
    public DbSet<LedgerAccount> LedgerAccounts => Set<LedgerAccount>();
    public DbSet<Posting> Postings => Set<Posting>();
    public DbSet<PostingLine> PostingLines => Set<PostingLine>();
    public DbSet<EventRecord> Events => Set<EventRecord>();
    public DbSet<OtcTrade> OtcTrades => Set<OtcTrade>();
    public DbSet<OnboardingLink> OnboardingLinks => Set<OnboardingLink>();
    public DbSet<Wallet> Wallets => Set<Wallet>();
    public DbSet<Transfer> Transfers => Set<Transfer>();
    public DbSet<DepositAccount> DepositAccounts => Set<DepositAccount>();
    public DbSet<Deposit> Deposits => Set<Deposit>();
    public DbSet<PayoutAccount> PayoutAccounts => Set<PayoutAccount>();
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<OperatorKey> OperatorKeys => Set<OperatorKey>();
    public DbSet<RefundRequest> RefundRequests => Set<RefundRequest>();
    public DbSet<WebhookEndpoint> WebhookEndpoints => Set<WebhookEndpoint>();
    public DbSet<EventDelivery> EventDeliveries => Set<EventDelivery>();

    /// <summary>The Data Protection key ring that encrypts webhook signing keys, shared by every instance.</summary>
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public const string IdentityPurpose = "Ndeipi.Payments.IdentityData.v1";

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);

        // Names, emails and phone numbers are stored encrypted with Data Protection (SRV-KYC-05), so
        // a database backup or a read-only SQL login sees ciphertext. The model is built once, so
        // this protector, from the shared key ring, serves every context.
        var protector = protection.CreateProtector(IdentityPurpose);
        // EF never hands a null to a converter, so one for non-null strings also serves the nullable columns.
        ValueConverter identity = new ValueConverter<string, string>(v => protector.Protect(v), v => protector.Unprotect(v));

        model.Entity<Integrator>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(200);
        });

        model.Entity<ApiKey>(e =>
        {
            e.Property(k => k.KeyHash).HasMaxLength(32);
            e.HasIndex(k => k.KeyHash).IsUnique();
            e.Property(k => k.Last4).HasMaxLength(4);
            e.Property(k => k.Environment).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Integrator>().WithMany().HasForeignKey(k => k.IntegratorId);
        });

        model.Entity<IdempotencyRecord>(e =>
        {
            e.Property(r => r.Key).HasMaxLength(255);
            e.Property(r => r.RequestHash).HasMaxLength(32);
            e.HasIndex(r => new { r.IntegratorId, r.Key }).IsUnique();
            e.HasIndex(r => r.ExpiresAt);
            e.HasQueryFilter(r => r.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<AuditEntry>(e =>
        {
            e.ToTable("AuditLog");
            e.Property(a => a.RequestId).HasMaxLength(40);
            e.Property(a => a.Method).HasMaxLength(10);
            e.Property(a => a.Path).HasMaxLength(400);
            e.Property(a => a.Operator).HasMaxLength(200);
            e.Property(a => a.IdempotencyKey).HasMaxLength(255);
            e.HasIndex(a => new { a.IntegratorId, a.CreatedAt });
        });

        model.Entity<PaymentUser>(e =>
        {
            e.ToTable("Users");
            e.Property(u => u.Id).HasMaxLength(40);
            e.Property(u => u.ExternalReference).HasMaxLength(128);
            e.HasIndex(u => new { u.IntegratorId, u.ExternalReference }).IsUnique();
            // Identity data is encrypted at rest (SRV-KYC-05); it is never searched by, only read back.
            e.Property(u => u.Email).HasMaxLength(1000).HasConversion(identity);
            e.Property(u => u.Phone).HasMaxLength(1000).HasConversion(identity);
            e.Property(u => u.FirstName).HasMaxLength(1000).HasConversion(identity);
            e.Property(u => u.LastName).HasMaxLength(1000).HasConversion(identity);
            e.Property(u => u.BusinessName).HasMaxLength(1000).HasConversion(identity);
            e.Property(u => u.Country).HasMaxLength(2);
            e.Property(u => u.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(u => u.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(u => u.KycStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(u => u.TermsStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(u => u.Version).IsConcurrencyToken();
            e.HasQueryFilter(u => u.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<LedgerAccount>(e =>
        {
            e.Property(a => a.Id).HasMaxLength(40);
            e.Property(a => a.WalletId).HasMaxLength(40);
            e.Property(a => a.Provider).HasMaxLength(40);
            e.Property(a => a.Asset).HasMaxLength(32);
            e.HasIndex(a => new { a.Kind, a.Provider, a.Asset });
            // One system account (suspense, points issued) per integrator, kind, bucket and asset.
            e.HasIndex(a => new { a.IntegratorId, a.Kind, a.Bucket, a.Asset }).IsUnique()
                .HasFilter("[WalletId] IS NULL AND [Provider] IS NULL");
            // And one clearing account per owner, provider and currency.
            e.HasIndex(a => new { a.IntegratorId, a.Provider, a.Asset }).IsUnique()
                .HasFilter("[Provider] IS NOT NULL");
            e.Property(a => a.Balance).HasPrecision(38, 18);
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Bucket).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(a => new { a.WalletId, a.Bucket }).IsUnique().HasFilter("[WalletId] IS NOT NULL");
            e.ToTable(t => t.HasCheckConstraint("CK_LedgerAccounts_NonNegative", "[AllowNegative] = 1 OR [Balance] >= 0"));
            e.HasQueryFilter(a => a.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<Posting>(e =>
        {
            e.Property(p => p.Id).HasMaxLength(40);
            e.Property(p => p.TransferId).HasMaxLength(40);
            e.Property(p => p.Description).HasMaxLength(500);
            e.Property(p => p.RequestId).HasMaxLength(40);
            e.Property(p => p.IdempotencyKey).HasMaxLength(255);
            e.Property(p => p.ReversesPostingId).HasMaxLength(40);
            e.HasIndex(p => new { p.IntegratorId, p.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            e.HasIndex(p => p.ReversesPostingId).IsUnique().HasFilter("[ReversesPostingId] IS NOT NULL");
            e.HasIndex(p => p.TransferId);
            e.HasMany(p => p.Lines).WithOne().HasForeignKey(l => l.PostingId);
            e.HasQueryFilter(p => p.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<PostingLine>(e =>
        {
            e.Property(l => l.PostingId).HasMaxLength(40);
            e.Property(l => l.AccountId).HasMaxLength(40);
            e.Property(l => l.Asset).HasMaxLength(32);
            e.Property(l => l.Amount).HasPrecision(38, 18);
            e.Property(l => l.BalanceAfter).HasPrecision(38, 18);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(l => l.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => new { l.AccountId, l.Id });
        });

        model.Entity<OnboardingLink>(e =>
        {
            e.Property(l => l.UserId).HasMaxLength(40);
            e.Property(l => l.Kind).HasConversion<string>().HasMaxLength(10);
            e.Property(l => l.Url).HasMaxLength(2000);
            e.HasIndex(l => new { l.UserId, l.Kind });
            e.HasQueryFilter(l => l.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<Wallet>(e =>
        {
            e.Property(w => w.Id).HasMaxLength(40);
            e.Property(w => w.UserId).HasMaxLength(40);
            e.Property(w => w.Asset).HasMaxLength(32);
            e.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(w => w.Version).IsConcurrencyToken();
            e.HasIndex(w => new { w.UserId, w.Asset }).IsUnique();
            e.HasQueryFilter(w => w.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<Transfer>(e =>
        {
            e.Property(t => t.Id).HasMaxLength(40);
            e.Property(t => t.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.State).HasConversion<string>().HasMaxLength(20);
            e.Property(t => t.SourceType).HasMaxLength(20);
            e.Property(t => t.SourceWalletId).HasMaxLength(40);
            e.Property(t => t.SourceUserId).HasMaxLength(40);
            e.Property(t => t.DestinationType).HasMaxLength(20);
            e.Property(t => t.DestinationWalletId).HasMaxLength(40);
            e.Property(t => t.DestinationUserId).HasMaxLength(40);
            e.Property(t => t.Asset).HasMaxLength(32);
            e.Property(t => t.Amount).HasPrecision(38, 18);
            e.Property(t => t.IntegratorReference).HasMaxLength(128);
            e.Property(t => t.IdempotencyKey).HasMaxLength(255);
            e.Property(t => t.DestinationAsset).HasMaxLength(32);
            e.Property(t => t.Rail).HasMaxLength(32);
            e.Property(t => t.PayoutAccountId).HasMaxLength(40);
            e.Property(t => t.DepositAccountId).HasMaxLength(40);
            e.Property(t => t.QuoteId).HasMaxLength(40);
            e.Property(t => t.ProviderReference).HasMaxLength(100);
            e.HasIndex(t => new { t.Kind, t.State });
            e.HasIndex(t => t.ProviderReference);
            e.Property(t => t.Version).IsConcurrencyToken();
            e.HasIndex(t => new { t.IntegratorId, t.IdempotencyKey }).IsUnique().HasFilter("[IdempotencyKey] IS NOT NULL");
            e.HasIndex(t => t.SourceWalletId);
            e.HasIndex(t => t.DestinationWalletId);
            e.HasIndex(t => t.SourceUserId);
            e.HasIndex(t => t.DestinationUserId);
            e.HasIndex(t => new { t.IntegratorId, t.IntegratorReference });
            e.HasQueryFilter(t => t.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<DepositAccount>(e =>
        {
            e.Property(d => d.Id).HasMaxLength(40);
            e.Property(d => d.UserId).HasMaxLength(40);
            e.Property(d => d.WalletId).HasMaxLength(40);
            e.Property(d => d.Currency).HasMaxLength(3);
            e.Property(d => d.Rail).HasMaxLength(32);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.ProviderReference).HasMaxLength(100);
            e.Property(d => d.Reference).HasMaxLength(40);
            e.HasIndex(d => d.Reference).IsUnique();
            e.HasIndex(d => d.UserId);
            e.Property(d => d.Version).IsConcurrencyToken();
            e.HasQueryFilter(d => d.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<Deposit>(e =>
        {
            e.Property(d => d.Id).HasMaxLength(40);
            e.Property(d => d.DepositAccountId).HasMaxLength(40);
            e.Property(d => d.TransferId).HasMaxLength(40);
            e.Property(d => d.AmountReceived).HasPrecision(38, 18);
            e.Property(d => d.Currency).HasMaxLength(3);
            e.Property(d => d.RailReference).HasMaxLength(100);
            e.HasIndex(d => d.DepositAccountId);
            e.HasQueryFilter(d => d.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<PayoutAccount>(e =>
        {
            e.Property(p => p.Id).HasMaxLength(40);
            e.Property(p => p.UserId).HasMaxLength(40);
            e.Property(p => p.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Currency).HasMaxLength(3);
            e.Property(p => p.Country).HasMaxLength(2);
            e.Property(p => p.Rail).HasMaxLength(32);
            e.Property(p => p.AccountOwnerName).HasMaxLength(1000).HasConversion(identity);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(p => p.UserId);
            e.Property(p => p.Version).IsConcurrencyToken();
            e.HasQueryFilter(p => p.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<Quote>(e =>
        {
            e.Property(q => q.Id).HasMaxLength(40);
            e.Property(q => q.UserId).HasMaxLength(40);
            e.Property(q => q.SourceWalletId).HasMaxLength(40);
            e.Property(q => q.DestinationWalletId).HasMaxLength(40);
            e.Property(q => q.From).HasMaxLength(32);
            e.Property(q => q.To).HasMaxLength(32);
            e.Property(q => q.Amount).HasPrecision(38, 18);
            e.Property(q => q.Fee).HasPrecision(38, 18);
            e.Property(q => q.AmountOut).HasPrecision(38, 18);
            e.Property(q => q.Rate).HasPrecision(38, 18);
            e.Property(q => q.Status).HasConversion<string>().HasMaxLength(10);
            e.Property(q => q.TransferId).HasMaxLength(40);
            e.HasQueryFilter(q => q.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<OperatorKey>(e =>
        {
            e.Property(k => k.Operator).HasMaxLength(200);
            e.Property(k => k.KeyHash).HasMaxLength(32);
            e.HasIndex(k => k.KeyHash).IsUnique();
        });

        // Operator work spans integrators, so refund requests carry their integrator but no query filter.
        model.Entity<RefundRequest>(e =>
        {
            e.Property(r => r.Id).HasMaxLength(40);
            e.Property(r => r.TransferId).HasMaxLength(40);
            e.Property(r => r.Amount).HasPrecision(38, 18);
            e.Property(r => r.Reason).HasMaxLength(500);
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.RequestedBy).HasMaxLength(200);
            e.Property(r => r.DecidedBy).HasMaxLength(200);
            e.Property(r => r.Version).IsConcurrencyToken();
            e.HasIndex(r => r.TransferId);
            e.ToTable(t => t.HasCheckConstraint("CK_RefundRequests_FourEyes", "[DecidedBy] IS NULL OR [DecidedBy] <> [RequestedBy]"));
        });

        model.Entity<WebhookEndpoint>(e =>
        {
            e.Property(w => w.Id).HasMaxLength(40);
            e.Property(w => w.Url).HasMaxLength(2000);
            e.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(w => w.Description).HasMaxLength(200);
            e.Property(w => w.Version).IsConcurrencyToken();
            e.HasIndex(w => new { w.IntegratorId, w.DeletedAt });
            e.HasQueryFilter(w => w.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<EventDelivery>(e =>
        {
            e.Property(d => d.EventId).HasMaxLength(40);
            e.Property(d => d.WebhookEndpointId).HasMaxLength(40);
            e.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(d => d.LastError).HasMaxLength(500);
            // The dispatcher's query: what is due now.
            e.HasIndex(d => new { d.Status, d.NextAttemptAt });
            e.HasIndex(d => d.EventId);
            e.HasQueryFilter(d => d.IntegratorId == CurrentIntegratorId);
        });

        model.Entity<OtcTrade>(e =>
        {
            e.Property(t => t.Side).HasConversion<string>().HasMaxLength(10);
            e.Property(t => t.CoinAmount).HasPrecision(38, 18);
            e.Property(t => t.UsdAmount).HasPrecision(38, 18);
            e.Property(t => t.UsdPerCoin).HasPrecision(38, 18);
            e.Property(t => t.AbsaReference).HasMaxLength(100);
            e.Property(t => t.DeskReference).HasMaxLength(100);
            e.HasIndex(t => t.DeskReference).IsUnique();
            e.Property(t => t.RecordedBy).HasMaxLength(200);
            e.HasIndex(t => t.ExecutedAt);
            e.ToTable(t => t.HasCheckConstraint("CK_OtcTrades_Positive", "[CoinAmount] > 0 AND [UsdAmount] > 0 AND [UsdPerCoin] > 0"));
        });

        model.Entity<EventRecord>(e =>
        {
            e.ToTable("Events");
            e.Property(v => v.Id).HasMaxLength(40);
            e.Property(v => v.Type).HasMaxLength(100);
            e.Property(v => v.ObjectType).HasMaxLength(40);
            e.Property(v => v.ObjectId).HasMaxLength(40);
            e.HasIndex(v => new { v.IntegratorId, v.CreatedAt });
            e.HasIndex(v => v.ObjectId);
            e.HasQueryFilter(v => v.IntegratorId == CurrentIntegratorId);
        });
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        Guard();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        Guard();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Append-only rows are never changed or deleted (SRV-LED-02, SRV-OPS-03), and a request can only
    /// write its own integrator's rows (SC-05): new rows take the caller's integrator, and a row for
    /// another integrator is refused.
    /// </summary>
    void Guard()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.Entity is IAppendOnly && entry.State is EntityState.Modified or EntityState.Deleted)
                throw new InvalidOperationException($"{entry.Metadata.ClrType.Name} rows are append-only.");

            if (entry.Entity is IIntegratorOwned owned && entry.State is EntityState.Added or EntityState.Modified && scope.IntegratorId is { } current)
            {
                if (owned.IntegratorId == Guid.Empty)
                    owned.IntegratorId = current;
                // A request may open one of Ndeipi's own accounts the first time it needs it; the ledger
                // alone moves their balances.
                else if (owned.IntegratorId != current && !(owned is LedgerAccount && owned.IntegratorId == IntegratorScope.House))
                    throw new InvalidOperationException("A request cannot write another integrator's data.");
            }
        }
    }
}
