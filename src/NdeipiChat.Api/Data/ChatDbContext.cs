using Microsoft.EntityFrameworkCore;

namespace NdeipiChat.Api.Data;

public sealed class ChatDbContext(DbContextOptions<ChatDbContext> options) : DbContext(options)
{
    public const string TokenQueueTrigger = "trg_TokenTransferQueue_StatusChanged";

    public DbSet<User> Users => Set<User>();
    public DbSet<UserWallet> UserWallets => Set<UserWallet>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMember> Members => Set<ConversationMember>();
    public DbSet<ChatMessage> Messages => Set<ChatMessage>();
    public DbSet<TokenTransfer> TokenTransfers => Set<TokenTransfer>();
    public DbSet<BankingProfile> BankingProfiles => Set<BankingProfile>();
    public DbSet<BankTransfer> BankTransfers => Set<BankTransfer>();
    public DbSet<MobileAuthCode> AuthCodes => Set<MobileAuthCode>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<ProcessedWebhookEvent> WebhookEvents => Set<ProcessedWebhookEvent>();
    public DbSet<LivestockAnimal> Livestock => Set<LivestockAnimal>();
    public DbSet<LivestockHealthAudit> HealthAudits => Set<LivestockHealthAudit>();
    public DbSet<OperatorSigningKey> OperatorKeys => Set<OperatorSigningKey>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<User>(e =>
        {
            e.HasIndex(u => u.ClerkUserId).IsUnique();
            e.HasIndex(u => u.Email);
            e.Property(u => u.ClerkUserId).HasMaxLength(64);
            e.Property(u => u.DisplayName).HasMaxLength(200);
            e.Property(u => u.Username).HasMaxLength(100);
            e.Property(u => u.Email).HasMaxLength(320);
            e.Property(u => u.AvatarUrl).HasMaxLength(1000);
            e.HasMany(u => u.Wallets).WithOne().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<UserWallet>(e =>
        {
            e.HasIndex(w => new { w.UserId, w.Chain }).IsUnique();
            e.Property(w => w.Chain).HasMaxLength(50);
            e.Property(w => w.Address).HasMaxLength(128);
        });

        model.Entity<Conversation>(e =>
        {
            e.Property(c => c.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Title).HasMaxLength(100);
            e.Property(c => c.DirectKey).HasMaxLength(80);
            e.HasIndex(c => c.DirectKey).IsUnique().HasFilter("[DirectKey] IS NOT NULL");
            e.HasMany(c => c.Members).WithOne().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ConversationMember>(e =>
        {
            e.HasKey(m => new { m.ConversationId, m.UserId });
            e.HasIndex(m => m.UserId);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ChatMessage>(e =>
        {
            e.Property(m => m.Seq).UseIdentityColumn();
            e.Property(m => m.Kind).HasMaxLength(64);
            e.HasIndex(m => new { m.ConversationId, m.Seq });
            e.HasIndex(m => new { m.SenderId, m.ClientMessageId }).IsUnique();
            e.HasOne<Conversation>().WithMany().HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(m => m.SenderId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<TokenTransfer>(e =>
        {
            // The trigger flags status changes for the API. EF must know it's there: SQL Server
            // refuses a bare OUTPUT clause on a table with triggers.
            e.ToTable("TokenTransferQueue", "ndeipi", t => t.HasTrigger(TokenQueueTrigger));
            e.Property(t => t.Operation).HasMaxLength(20);
            e.Property(t => t.Status).HasMaxLength(20);
            e.Property(t => t.Chain).HasMaxLength(50);
            e.Property(t => t.TokenStandard).HasMaxLength(20);
            e.Property(t => t.TokenSymbol).HasMaxLength(32);
            e.Property(t => t.ContractAddress).HasMaxLength(128);
            e.Property(t => t.TokenId).HasMaxLength(78);
            e.Property(t => t.Amount).HasPrecision(38, 18);
            e.Property(t => t.SenderClerkId).HasMaxLength(64);
            e.Property(t => t.SenderWalletAddress).HasMaxLength(128);
            e.Property(t => t.RecipientClerkId).HasMaxLength(64);
            e.Property(t => t.RecipientWalletAddress).HasMaxLength(128);
            e.Property(t => t.Memo).HasMaxLength(280);
            e.Property(t => t.TxHash).HasMaxLength(128);
            e.Property(t => t.Error).HasMaxLength(1000);
            e.Property(t => t.ClaimedBy).HasMaxLength(100);
            e.Property(t => t.CreatedAt).HasColumnType("datetime2");
            e.Property(t => t.UpdatedAt).HasColumnType("datetime2");
            e.Property(t => t.ClaimedAt).HasColumnType("datetime2");
            e.Property(t => t.CompletedAt).HasColumnType("datetime2");
            e.HasIndex(t => new { t.Status, t.CreatedAt });
            e.HasIndex(t => t.MessageId);
            e.HasIndex(t => t.NotifyPending).HasFilter("[NotifyPending] = 1");
        });

        model.Entity<BankingProfile>(e =>
        {
            e.HasKey(p => p.UserId);
            e.HasOne<User>().WithOne().HasForeignKey<BankingProfile>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(p => p.BridgeCustomerId).HasMaxLength(100);
            e.Property(p => p.KycLinkId).HasMaxLength(100);
            e.Property(p => p.KycStatus).HasMaxLength(30);
            e.Property(p => p.TosStatus).HasMaxLength(30);
            e.Property(p => p.KycUrl).HasMaxLength(2000);
            e.Property(p => p.TosUrl).HasMaxLength(2000);
            e.Property(p => p.WalletId).HasMaxLength(100);
            e.Property(p => p.WalletChain).HasMaxLength(50);
            e.Property(p => p.WalletAddress).HasMaxLength(128);
            e.HasIndex(p => p.KycLinkId);
            e.HasIndex(p => p.BridgeCustomerId);
        });

        model.Entity<BankTransfer>(e =>
        {
            e.Property(t => t.Amount).HasPrecision(38, 6);
            e.Property(t => t.Currency).HasMaxLength(10);
            e.Property(t => t.Memo).HasMaxLength(280);
            e.Property(t => t.BridgeTransferId).HasMaxLength(100);
            e.Property(t => t.Status).HasMaxLength(20);
            e.Property(t => t.ProviderState).HasMaxLength(40);
            e.Property(t => t.Error).HasMaxLength(500);
            e.HasIndex(t => t.BridgeTransferId).IsUnique().HasFilter("[BridgeTransferId] IS NOT NULL");
            e.HasIndex(t => t.MessageId);
            e.HasIndex(t => t.Status);
        });

        model.Entity<MobileAuthCode>(e =>
        {
            e.HasKey(c => c.CodeHash);
            e.Property(c => c.CodeHash).HasMaxLength(64);
            e.Property(c => c.ClerkSessionId).HasMaxLength(64);
            e.Property(c => c.RedirectUri).HasMaxLength(200);
            e.Property(c => c.CodeChallenge).HasMaxLength(128);
        });

        model.Entity<RefreshToken>(e =>
        {
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.Property(t => t.ClerkSessionId).HasMaxLength(64);
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.ClerkSessionId);
        });

        model.Entity<ProcessedWebhookEvent>(e =>
        {
            e.HasKey(w => w.EventId);
            e.Property(w => w.EventId).HasMaxLength(100);
        });

        // Table and column names follow the livestock addendum's schema.
        model.Entity<LivestockAnimal>(e =>
        {
            e.ToTable("LivestockMaster");
            e.HasKey(c => c.CowId);
            e.Property(c => c.CowId).HasMaxLength(64);
            e.Property(c => c.RanchId).HasMaxLength(64);
            e.HasIndex(c => c.RanchId).HasDatabaseName("IX_Livestock_RanchId");
            e.Property(c => c.OwnerWallet).HasMaxLength(42);
            e.HasIndex(c => c.OwnerUserId);
            e.HasOne<User>().WithMany().HasForeignKey(c => c.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(c => c.BiometricMuzzleHash).HasMaxLength(64);
            e.HasIndex(c => c.BiometricMuzzleHash).IsUnique();
            e.Property(c => c.EmbeddingModel).HasMaxLength(100);
            e.Property(c => c.Breed).HasMaxLength(40);
            e.Property(c => c.BreedConfidence).HasPrecision(5, 4);
            e.Property(c => c.BreedSource).HasMaxLength(20);
            e.Property(c => c.Sex).HasMaxLength(10);
            e.Property(c => c.RegistrationTimestamp).HasColumnType("datetime2");
            e.Property(c => c.Latitude).HasPrecision(9, 6);
            e.Property(c => c.Longitude).HasPrecision(9, 6);
            e.Property(c => c.GpsAccuracyMeters).HasPrecision(8, 2);
            e.Property(c => c.FaceImageRef).HasMaxLength(256);
        });

        model.Entity<LivestockHealthAudit>(e =>
        {
            e.ToTable("LivestockHealthAudit");
            e.HasKey(a => a.AuditId);
            e.Property(a => a.CowId).HasMaxLength(64);
            e.HasOne<LivestockAnimal>().WithMany().HasForeignKey(a => a.CowId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(a => new { a.CowId, a.TimestampUtc });
            e.Property(a => a.TimestampUtc).HasColumnType("datetime2");
            e.Property(a => a.ClientCapturedAtUtc).HasColumnType("datetime2");
            e.Property(a => a.BodyConditionScore).HasPrecision(3, 1);
            e.Property(a => a.HealthRating).HasMaxLength(20);
            e.Property(a => a.HydrationStatus).HasMaxLength(30);
            e.Property(a => a.FaceImageRef).HasMaxLength(256);
            e.Property(a => a.FlankImageRef).HasMaxLength(256);
            e.Property(a => a.AttestationHash).HasMaxLength(64);
            e.Property(a => a.Signature).HasMaxLength(200);
            e.Property(a => a.SubmissionHash).HasMaxLength(64);
            e.HasIndex(a => a.SubmissionHash).IsUnique();
            e.Property(a => a.AssessmentModel).HasMaxLength(100);
        });

        model.Entity<OperatorSigningKey>(e =>
        {
            e.ToTable("LivestockOperatorKeys");
            e.Property(k => k.PublicKey).HasMaxLength(200);
            e.Property(k => k.Label).HasMaxLength(100);
            e.HasIndex(k => k.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(k => k.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
