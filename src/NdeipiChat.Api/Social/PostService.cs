using System.Numerics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Contracts;
using SkiaSharp;

namespace NdeipiChat.Api.Social;

/// <summary>A photo as uploaded, before it's checked.</summary>
public sealed record UploadedPhoto(string FileName, byte[] Data);

/// <summary>
/// Posts: publishing, the feed, likes, deleting, and minting a post as an NFT. A mint is a row in
/// the same queue as token transfers (Operation "Mint"), so Ndeipi Enterprise Server mints posts with
/// the workers it already runs; TokenTransferQueueWatcher relays its progress to the author.
/// </summary>
public sealed class PostService(ChatDbContext db, PostMediaStore media, IOptions<SocialOptions> options, TimeProvider clock)
{
    public const int PageSize = 20;

    NftOptions Nft => options.Value.Nft;

    public async Task<PostDto> CreateAsync(User author, string? caption, IReadOnlyList<UploadedPhoto> photos, bool mint, Uri site, CancellationToken ct)
    {
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (caption?.Length > SocialContract.MaxCaptionLength)
            throw new ChatRejectedException($"Captions are limited to {SocialContract.MaxCaptionLength} characters.");
        if (photos.Count == 0)
            throw new ChatRejectedException("Add at least one photo.");
        if (photos.Count > SocialContract.MaxPhotos)
            throw new ChatRejectedException($"A post can have up to {SocialContract.MaxPhotos} photos.");
        if (mint && !Nft.Enabled)
            throw new ChatRejectedException("Minting posts isn't available yet.");

        // Check every photo before storing any, so a bad fourth photo doesn't leave three orphans.
        var decoded = new List<SKBitmap>();
        try
        {
            foreach (var photo in photos)
            {
                if (photo.Data.Length > SocialContract.MaxPhotoBytes)
                    throw new ChatRejectedException($"{photo.FileName} is larger than {SocialContract.MaxPhotoBytes / (1024 * 1024)} MB.");
                var bitmap = CattleImages.DecodeUpright(photo.Data)
                    ?? throw new ChatRejectedException($"{photo.FileName} isn't a photo we can read. Use JPEG, PNG or WebP.");
                decoded.Add(bitmap);
                if (Math.Min(bitmap.Width, bitmap.Height) < SocialContract.MinPhotoShortSide)
                    throw new ChatRejectedException($"{photo.FileName} is too small. Photos need at least {SocialContract.MinPhotoShortSide} pixels on each side.");
            }

            var post = new Post { Id = Guid.NewGuid(), AuthorId = author.Id, Caption = caption, CreatedAt = clock.GetUtcNow() };
            for (var i = 0; i < decoded.Count; i++)
            {
                var item = new PostMedia { Id = Guid.NewGuid(), PostId = post.Id, Position = i, Width = decoded[i].Width, Height = decoded[i].Height };
                await media.SaveAsync(item.Id, decoded[i], ct);
                post.Media.Add(item);
            }

            db.Posts.Add(post);
            if (mint)
                QueueMint(post, author, await WalletOnNftChainAsync(author.Id, ct), site);
            await db.SaveChangesAsync(ct);
            return (await GetAsync(author.Id, post.Id, site, ct))!;
        }
        finally
        {
            foreach (var bitmap in decoded)
                bitmap.Dispose();
        }
    }

    /// <summary>Newest first; <paramref name="before"/> is the last post of the previous page.</summary>
    public async Task<FeedPageDto> FeedAsync(Guid me, Guid? before, Guid? authorId, Uri site, CancellationToken ct)
    {
        var query = db.Posts.AsNoTracking();
        if (authorId is { } author)
            query = query.Where(p => p.AuthorId == author);
        if (before is { } anchorId && await db.Posts.Where(p => p.Id == anchorId).Select(p => (DateTimeOffset?)p.CreatedAt).FirstOrDefaultAsync(ct) is { } anchor)
            query = query.Where(p => p.CreatedAt < anchor);

        var page = await Project(query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id).Take(PageSize + 1), me).ToListAsync(ct);
        return new FeedPageDto(
            page.Take(PageSize).Select(p => ToDto(p, site)).ToList(),
            page.Count > PageSize,
            Nft.Enabled,
            Nft.Enabled ? Nft.Chain : null);
    }

    public async Task<PostDto?> GetAsync(Guid me, Guid postId, Uri site, CancellationToken ct) =>
        await Project(db.Posts.AsNoTracking().Where(p => p.Id == postId), me).FirstOrDefaultAsync(ct) is { } row ? ToDto(row, site) : null;

    public async Task<LikeResultDto> SetLikeAsync(Guid me, Guid postId, bool like, CancellationToken ct)
    {
        if (!await db.Posts.AnyAsync(p => p.Id == postId, ct))
            throw new ChatRejectedException("That post has been deleted.");

        var existing = await db.PostLikes.FindAsync([postId, me], ct);
        if (like && existing is null)
        {
            db.PostLikes.Add(new PostLike { PostId = postId, UserId = me, CreatedAt = clock.GetUtcNow() });
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Liked twice at once; the like is there either way.
                db.ChangeTracker.Clear();
            }
        }
        else if (!like && existing is not null)
        {
            db.PostLikes.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        return new LikeResultDto(postId, await db.PostLikes.CountAsync(l => l.PostId == postId, ct), like);
    }

    /// <summary>
    /// Queues the post to be minted to its author. Minting again while it's queued, minting or done
    /// changes nothing; after a failure it queues a fresh attempt with the same token id.
    /// </summary>
    public async Task<PostDto> MintAsync(User me, Guid postId, Uri site, CancellationToken ct)
    {
        if (!Nft.Enabled)
            throw new ChatRejectedException("Minting posts isn't available yet.");
        var post = await db.Posts.Include(p => p.Mint).FirstOrDefaultAsync(p => p.Id == postId, ct)
            ?? throw new ChatRejectedException("That post has been deleted.");
        if (post.AuthorId != me.Id)
            throw new ChatRejectedException("Only the person who posted it can mint it.");

        if (post.Mint is null || post.Mint.Status == TransferStatuses.Failed)
        {
            QueueMint(post, me, await WalletOnNftChainAsync(me.Id, ct), site);
            await db.SaveChangesAsync(ct);
        }
        return (await GetAsync(me.Id, postId, site, ct))!;
    }

    /// <summary>A post that's minted or being minted stays: its NFT points at its photos.</summary>
    public async Task DeleteAsync(User me, Guid postId, CancellationToken ct)
    {
        var post = await db.Posts.Include(p => p.Media).Include(p => p.Mint).FirstOrDefaultAsync(p => p.Id == postId, ct);
        if (post is null)
            return;
        if (post.AuthorId != me.Id)
            throw new ChatRejectedException("You can only delete your own posts.");
        if (post.Mint is not null && post.Mint.Status != TransferStatuses.Failed)
            throw new ChatRejectedException("A minted post can't be deleted: its NFT shows these photos.");

        var mediaIds = post.Media.Select(m => m.Id).ToList();
        // A failed mint row stays in the queue as history; it just no longer points at a post.
        post.MintTransferId = null;
        db.Posts.Remove(post);
        await db.SaveChangesAsync(ct);
        foreach (var id in mediaIds)
            media.Delete(id);
    }

    /// <summary>The ERC-721 metadata a minted post's tokenURI points at; null if the post isn't minted.</summary>
    public async Task<object?> MetadataAsync(Guid postId, Uri site, CancellationToken ct)
    {
        var post = await db.Posts.AsNoTracking()
            .Include(p => p.Author)
            .Include(p => p.Media)
            .Include(p => p.Mint)
            .FirstOrDefaultAsync(p => p.Id == postId && p.MintTransferId != null, ct);
        if (post is null)
            return null;

        var photos = post.Media.OrderBy(m => m.Position).Select(m => Absolute(site, SocialContract.MediaPath(m.Id, PhotoSizes.Full))).ToList();
        return new
        {
            name = $"{Nft.CollectionName} #{ShortId(post.Id)}",
            description = post.Caption ?? $"A post by {post.Author.DisplayName} on Ndeipi.",
            image = photos[0],
            external_url = Absolute(site, $"posts/{post.Id}"),
            attributes = new object[]
            {
                new { trait_type = "Author", value = post.Author.DisplayName },
                new { trait_type = "Photos", value = photos.Count },
                new { trait_type = "Posted", display_type = "date", value = post.CreatedAt.ToUnixTimeSeconds() }
            },
            properties = new { images = photos }
        };
    }

    /// <summary>The site's public address: configured, or the one this request came in on.</summary>
    public Uri SiteFor(HttpRequest request) => options.Value.PublicBaseUrl.Length > 0
        ? new Uri(options.Value.PublicBaseUrl.TrimEnd('/') + "/")
        : new Uri($"{request.Scheme}://{request.Host}{request.PathBase}/");

    /// <summary>The post's id as an unsigned 128-bit number: unique, and the same on every retry.</summary>
    public static string TokenIdFor(Guid postId) => new BigInteger(postId.ToByteArray(), isUnsigned: true).ToString();

    void QueueMint(Post post, User author, string? wallet, Uri site)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var mint = new TokenTransfer
        {
            Id = Guid.NewGuid(),
            Operation = TokenTransfer.MintOperation,
            Chain = Nft.Chain.ToLowerInvariant(),
            TokenStandard = Nft.Standard.ToLowerInvariant(),
            TokenSymbol = Nft.Symbol,
            ContractAddress = Nft.ContractAddress,
            TokenId = TokenIdFor(post.Id),
            Amount = 1,
            // Minted to the author, who is also the one asking for it.
            SenderUserId = author.Id,
            SenderClerkId = author.ClerkUserId,
            SenderWalletAddress = wallet,
            RecipientUserId = author.Id,
            RecipientClerkId = author.ClerkUserId,
            RecipientWalletAddress = wallet,
            PostId = post.Id,
            MetadataUri = Absolute(site, SocialContract.MetadataPath(post.Id)),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TokenTransfers.Add(mint);
        post.MintTransferId = mint.Id;
        post.Mint = mint;
    }

    Task<string?> WalletOnNftChainAsync(Guid userId, CancellationToken ct)
    {
        var chain = Nft.Chain.ToLowerInvariant();
        return db.UserWallets.Where(w => w.UserId == userId && w.Chain == chain).Select(w => w.Address).FirstOrDefaultAsync(ct);
    }

    sealed record Row(Post Post, User Author, List<PostMedia> Media, int Likes, bool Liked, TokenTransfer? Mint);

    IQueryable<Row> Project(IQueryable<Post> posts, Guid me) => posts.Select(p => new Row(
        p,
        p.Author,
        p.Media.OrderBy(m => m.Position).ToList(),
        db.PostLikes.Count(l => l.PostId == p.Id),
        db.PostLikes.Any(l => l.PostId == p.Id && l.UserId == me),
        p.Mint));

    static PostDto ToDto(Row row, Uri site) => new(
        row.Post.Id,
        ChatMapper.ToDto(row.Author),
        row.Post.Caption,
        row.Media.Select(m => new PostMediaDto(m.Id, m.Width, m.Height, Absolute(site, SocialContract.MediaPath(m.Id, PhotoSizes.Feed)))).ToList(),
        row.Post.CreatedAt,
        row.Likes,
        row.Liked,
        row.Mint is null ? null : ToNftDto(row.Mint));

    public static PostNftDto ToNftDto(TokenTransfer mint) => new(
        mint.PostId!.Value, mint.Status, mint.Chain, mint.TokenStandard, mint.ContractAddress, mint.TokenId!, mint.MetadataUri!, mint.TxHash, mint.Error);

    static string Absolute(Uri site, string path) => new Uri(site, path).ToString();

    static string ShortId(Guid id) => id.ToString("N")[..8].ToUpperInvariant();
}
