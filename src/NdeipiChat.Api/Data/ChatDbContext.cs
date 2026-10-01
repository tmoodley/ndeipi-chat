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
    public DbSet<ShamwariLink> Shamwaris => Set<ShamwariLink>();
    public DbSet<Post> Posts => Set<Post>();
    public DbSet<PostMedia> PostMedia => Set<PostMedia>();
    public DbSet<PostLike> PostLikes => Set<PostLike>();
    public DbSet<PostComment> PostComments => Set<PostComment>();
    public DbSet<PosMerchant> PosMerchants => Set<PosMerchant>();
    public DbSet<PosStore> PosStores => Set<PosStore>();
    public DbSet<PosStaff> PosStaff => Set<PosStaff>();
    public DbSet<PosTillSession> PosTillSessions => Set<PosTillSession>();
    public DbSet<PosCategory> PosCategories => Set<PosCategory>();
    public DbSet<PosProduct> PosProducts => Set<PosProduct>();
    public DbSet<PosStock> PosStock => Set<PosStock>();
    public DbSet<PosShift> PosShifts => Set<PosShift>();
    public DbSet<PosCashMovement> PosCashMovements => Set<PosCashMovement>();
    public DbSet<PosSale> PosSales => Set<PosSale>();
    public DbSet<PosSaleLine> PosSaleLines => Set<PosSaleLine>();
    public DbSet<PosPayment> PosPayments => Set<PosPayment>();
    public DbSet<PosAuditEntry> PosAudit => Set<PosAuditEntry>();
    public DbSet<PosQrPayment> PosQrPayments => Set<PosQrPayment>();
    public DbSet<DonationCampaign> DonationCampaigns => Set<DonationCampaign>();
    public DbSet<Donation> Donations => Set<Donation>();
    public DbSet<PointsEntry> Points => Set<PointsEntry>();
    public DbSet<LoanApplication> LoanApplications => Set<LoanApplication>();
    public DbSet<LoanApplicationItem> LoanApplicationItems => Set<LoanApplicationItem>();
    public DbSet<LoanDocument> LoanDocuments => Set<LoanDocument>();
    public DbSet<LoanDecision> LoanDecisions => Set<LoanDecision>();
    public DbSet<LoanCommittee> LoanCommittees => Set<LoanCommittee>();
    public DbSet<LoanCommitteeMember> LoanCommitteeMembers => Set<LoanCommitteeMember>();
    public DbSet<LoanEquipment> LoanEquipment => Set<LoanEquipment>();
    public DbSet<FinanceConstituency> FinanceConstituencies => Set<FinanceConstituency>();
    public DbSet<TrustLink> TrustLinks => Set<TrustLink>();
    public DbSet<TrustScoreRecord> TrustScores => Set<TrustScoreRecord>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<EventListing> Events => Set<EventListing>();
    public DbSet<TicketTier> TicketTiers => Set<TicketTier>();
    public DbSet<TicketHold> TicketHolds => Set<TicketHold>();
    public DbSet<TicketOrder> TicketOrders => Set<TicketOrder>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<EventValidator> EventValidators => Set<EventValidator>();
    public DbSet<GigProfile> GigProfiles => Set<GigProfile>();
    public DbSet<Gig> Gigs => Set<Gig>();
    public DbSet<GigOffer> GigOffers => Set<GigOffer>();
    public DbSet<Follow> Follows => Set<Follow>();
    public DbSet<SocialGroup> SocialGroups => Set<SocialGroup>();
    public DbSet<GroupMember> GroupMembers => Set<GroupMember>();

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
            e.Property(u => u.Phone).HasMaxLength(20);
            e.HasIndex(u => u.Phone);
            e.Property(u => u.AvatarUrl).HasMaxLength(1000);
            e.Property(u => u.Roles).HasMaxLength(400);
            e.Property(u => u.PinnedApps).HasMaxLength(400);
            e.Property(u => u.Bio).HasMaxLength(500);
            e.Property(u => u.City).HasMaxLength(80);
            e.Property(u => u.Website).HasMaxLength(200);
            e.Property(u => u.ClerkAvatarUrl).HasMaxLength(1000);
            e.Property(u => u.CoverUrl).HasMaxLength(1000);
            e.HasMany(u => u.Wallets).WithOne().HasForeignKey(w => w.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Post>(e =>
        {
            e.Property(p => p.Caption).HasMaxLength(2200);
            e.HasIndex(p => new { p.CreatedAt, p.Id });
            e.HasIndex(p => new { p.AuthorId, p.CreatedAt });
            e.HasOne(p => p.Author).WithMany().HasForeignKey(p => p.AuthorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(p => p.Mint).WithMany().HasForeignKey(p => p.MintTransferId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(p => p.Media).WithOne().HasForeignKey(m => m.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(p => new { p.GroupId, p.CreatedAt });
            // Deleting a post deletes its reposts in PostService (SQL Server won't cascade a table onto itself).
            e.HasOne(p => p.RepostOf).WithMany().HasForeignKey(p => p.RepostOfId).OnDelete(DeleteBehavior.NoAction);
            // One repost of a post per person.
            e.HasIndex(p => new { p.AuthorId, p.RepostOfId }).IsUnique().HasFilter("[RepostOfId] IS NOT NULL");
            e.HasIndex(p => p.RepostOfId);
            e.HasOne(p => p.Group).WithMany().HasForeignKey(p => p.GroupId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Follow>(e =>
        {
            e.HasKey(f => new { f.FollowerId, f.FolloweeId });
            e.HasIndex(f => f.FolloweeId);
            e.HasOne<User>().WithMany().HasForeignKey(f => f.FollowerId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(f => f.FolloweeId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Follows_NotSelf", "[FollowerId] <> [FolloweeId]"));
        });

        model.Entity<SocialGroup>(e =>
        {
            e.ToTable("Groups");
            e.Property(g => g.Name).HasMaxLength(80);
            e.Property(g => g.Description).HasMaxLength(1000);
            e.Property(g => g.Rules).HasMaxLength(2000);
            e.Property(g => g.Icon).HasMaxLength(16);
            e.Property(g => g.Tone).HasMaxLength(16);
            e.Property(g => g.Tagline).HasMaxLength(120);
            e.Property(g => g.Email).HasMaxLength(320);
            e.Property(g => g.Website).HasMaxLength(200);
            e.Property(g => g.AvatarUrl).HasMaxLength(1000);
            e.Property(g => g.CoverUrl).HasMaxLength(1000);
            e.HasIndex(g => g.Name);
            e.HasOne(g => g.Owner).WithMany().HasForeignKey(g => g.OwnerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(g => g.Members).WithOne().HasForeignKey(m => m.GroupId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<GroupMember>(e =>
        {
            e.HasKey(m => new { m.GroupId, m.UserId });
            e.HasIndex(m => m.UserId);
            e.Property(m => m.Role).HasMaxLength(16);
            e.HasOne(m => m.User).WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<InventoryItem>(e =>
        {
            e.Property(i => i.Name).HasMaxLength(120);
            e.Property(i => i.Sku).HasMaxLength(64);
            e.Property(i => i.Location).HasMaxLength(120);
            e.HasIndex(i => new { i.OwnerId, i.UpdatedAt });
            e.HasOne<User>().WithMany().HasForeignKey(i => i.OwnerId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<EventListing>(e =>
        {
            e.ToTable("Events");
            e.Property(v => v.Title).HasMaxLength(160);
            e.Property(v => v.Description).HasMaxLength(4000);
            e.Property(v => v.Category).HasMaxLength(32);
            e.Property(v => v.City).HasMaxLength(80);
            e.Property(v => v.Venue).HasMaxLength(160);
            e.Property(v => v.Currency).HasMaxLength(16);
            e.HasIndex(v => new { v.IsPublished, v.City, v.StartsAt });
            e.HasIndex(v => new { v.OrganizerId, v.StartsAt });
            e.HasOne(v => v.Organizer).WithMany().HasForeignKey(v => v.OrganizerId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(v => v.Tiers).WithOne().HasForeignKey(t => t.EventId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<TicketTier>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(80);
            e.Property(t => t.Price).HasPrecision(18, 2);
            // Under the conditional updates, the database itself refuses to oversell.
            e.ToTable(t => t.HasCheckConstraint("CK_TicketTiers_Capacity", "[Sold] >= 0 AND [Held] >= 0 AND [Sold] + [Held] <= [Capacity]"));
        });

        model.Entity<TicketHold>(e =>
        {
            e.Property(h => h.Amount).HasPrecision(18, 2);
            e.Property(h => h.Status).HasMaxLength(16);
            e.HasIndex(h => new { h.Status, h.ExpiresAt });
            e.HasIndex(h => new { h.UserId, h.CreatedAt });
            e.HasOne<TicketTier>().WithMany().HasForeignKey(h => h.TierId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(h => h.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<TicketOrder>(e =>
        {
            e.Property(o => o.Amount).HasPrecision(18, 2);
            e.Property(o => o.Currency).HasMaxLength(16);
            e.Property(o => o.Status).HasMaxLength(16);
            e.Property(o => o.Error).HasMaxLength(400);
            e.HasIndex(o => o.HoldId).IsUnique();
            e.HasIndex(o => new { o.BuyerId, o.CreatedAt });
            e.HasOne<TicketHold>().WithMany().HasForeignKey(o => o.HoldId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(o => o.BuyerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<Ticket>(e =>
        {
            e.Property(t => t.Status).HasMaxLength(16);
            e.Property(t => t.HolderPublicKey).HasMaxLength(200);
            e.Property(t => t.AdmittedBy).HasMaxLength(120);
            e.HasIndex(t => new { t.OwnerId, t.CreatedAt });
            e.HasIndex(t => t.EventId);
            e.HasOne<TicketOrder>().WithMany().HasForeignKey(t => t.OrderId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(t => t.OwnerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<EventValidator>(e =>
        {
            e.HasKey(v => new { v.EventId, v.UserId });
            e.HasOne<EventListing>().WithMany().HasForeignKey(v => v.EventId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(v => v.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<GigProfile>(e =>
        {
            e.HasKey(p => p.UserId);
            e.Property(p => p.Headline).HasMaxLength(160);
            e.Property(p => p.Skills).HasMaxLength(200);
            e.Property(p => p.Region).HasMaxLength(80);
            // Dispatch narrows by a latitude band before measuring distances.
            e.HasIndex(p => new { p.IsAvailable, p.Latitude });
            e.HasOne(p => p.User).WithOne().HasForeignKey<GigProfile>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<Gig>(e =>
        {
            e.Property(g => g.Title).HasMaxLength(120);
            e.Property(g => g.Description).HasMaxLength(2000);
            e.Property(g => g.Skill).HasMaxLength(32);
            e.Property(g => g.Region).HasMaxLength(80);
            e.Property(g => g.Budget).HasPrecision(38, 18);
            e.Property(g => g.TokenSymbol).HasMaxLength(20);
            e.Property(g => g.Status).HasMaxLength(16);
            e.HasIndex(g => new { g.ClientId, g.CreatedAt });
            e.HasIndex(g => new { g.WorkerId, g.CreatedAt });
            e.HasIndex(g => new { g.Status, g.Latitude });
            e.HasOne(g => g.Client).WithMany().HasForeignKey(g => g.ClientId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(g => g.Worker).WithMany().HasForeignKey(g => g.WorkerId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("CK_Gigs_Stars", "([WorkerStars] IS NULL OR [WorkerStars] BETWEEN 1 AND 5) AND ([ClientStars] IS NULL OR [ClientStars] BETWEEN 1 AND 5)"));
        });

        model.Entity<GigOffer>(e =>
        {
            e.HasKey(o => new { o.GigId, o.WorkerId });
            e.Property(o => o.Status).HasMaxLength(16);
            e.HasIndex(o => new { o.WorkerId, o.Status });
            e.HasOne<Gig>().WithMany().HasForeignKey(o => o.GigId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(o => o.WorkerId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<PostMedia>(e => e.HasIndex(m => new { m.PostId, m.Position }).IsUnique());

        NdeipiChat.Api.Pos.PosModel.Configure(model);
        NdeipiChat.Api.Donations.DonationsModel.Configure(model);
        NdeipiChat.Api.Finance.FinanceModel.Configure(model);
        NdeipiChat.Api.Trust.TrustModel.Configure(model);

        model.Entity<PostComment>(e =>
        {
            e.Property(c => c.Text).HasMaxLength(NdeipiChat.Contracts.SocialContract.MaxCommentLength);
            e.HasIndex(c => new { c.PostId, c.CreatedAt });
            e.HasIndex(c => new { c.AuthorId, c.CreatedAt });
            e.HasOne<Post>().WithMany().HasForeignKey(c => c.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.Author).WithMany().HasForeignKey(c => c.AuthorId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<PostLike>(e =>
        {
            e.HasKey(l => new { l.PostId, l.UserId });
            e.HasOne<Post>().WithMany().HasForeignKey(l => l.PostId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(l => l.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        model.Entity<ShamwariLink>(e =>
        {
            e.ToTable("Shamwaris");
            e.HasOne(s => s.Requester).WithMany().HasForeignKey(s => s.RequesterId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Addressee).WithMany().HasForeignKey(s => s.AddresseeId).OnDelete(DeleteBehavior.Restrict);
            e.Property(s => s.InviteContact).HasMaxLength(320);
            e.Property(s => s.PairKey).HasMaxLength(80);
            e.HasIndex(s => s.PairKey).IsUnique().HasFilter("[PairKey] IS NOT NULL");
            e.HasIndex(s => new { s.RequesterId, s.InviteContact }).IsUnique().HasFilter("[InviteContact] IS NOT NULL");
            e.HasIndex(s => s.InviteContact).HasFilter("[InviteContact] IS NOT NULL");
            e.HasIndex(s => s.AddresseeId);
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
            e.Property(t => t.MetadataUri).HasMaxLength(500);
            e.HasIndex(t => t.PostId).HasFilter("[PostId] IS NOT NULL");
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
            e.HasIndex(t => t.OrderId).HasFilter("[OrderId] IS NOT NULL");
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
