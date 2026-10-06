using Microsoft.EntityFrameworkCore;

namespace Ndeipi.Payments.Data;

/// <summary>
/// Whose data this request may touch: set from the API key on every API request (SC-05).
/// Background work that spans integrators (dispatch, reconciliation) leaves it empty and opts out
/// of the filters explicitly with <c>IgnoreQueryFilters()</c>.
/// </summary>
public sealed class IntegratorScope
{
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

public sealed class PaymentsDbContext(DbContextOptions<PaymentsDbContext> options, IntegratorScope scope) : DbContext(options)
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

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.HasDefaultSchema(Schema);

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
            e.Property(u => u.Email).HasMaxLength(320);
            e.Property(u => u.Phone).HasMaxLength(20);
            e.Property(u => u.FirstName).HasMaxLength(100);
            e.Property(u => u.LastName).HasMaxLength(100);
            e.Property(u => u.BusinessName).HasMaxLength(200);
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
                else if (owned.IntegratorId != current)
                    throw new InvalidOperationException("A request cannot write another integrator's data.");
            }
        }
    }
}
