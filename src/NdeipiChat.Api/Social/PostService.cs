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

    /// <param name="groupId">Post in this group; the author must be a member.</param>
    public async Task<PostDto> CreateAsync(User author, string? caption, IReadOnlyList<UploadedPhoto> photos, bool mint, Uri site, CancellationToken ct, Guid? groupId = null)
    {
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (caption?.Length > SocialContract.MaxCaptionLength)
            throw new ChatRejectedException($"Captions are limited to {SocialContract.MaxCaptionLength} characters.");
        if (photos.Count == 0 && caption is null)
            throw new ChatRejectedException("Write something or add a photo.");
        if (photos.Count > SocialContract.MaxPhotos)
            throw new ChatRejectedException($"A post can have up to {SocialContract.MaxPhotos} photos.");
        if (mint && !Nft.Enabled)
            throw new ChatRejectedException("Minting posts isn't available yet.");
        if (mint && photos.Count == 0)
            throw new ChatRejectedException("Only posts with a photo can be minted.");
        if (groupId is { } g && !await db.GroupMembers.AnyAsync(m => m.GroupId == g && m.UserId == author.Id, ct))
            throw new ChatRejectedException("Join the group to post in it.");

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

            var post = new Post { Id = Guid.NewGuid(), AuthorId = author.Id, Caption = caption, GroupId = groupId, CreatedAt = clock.GetUtcNow() };
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

    /// <summary>
    /// Newest first; <paramref name="before"/> is the last post of the previous page. Posts in
    /// private groups only reach members. <paramref name="scope"/> "following" narrows it to you,
    /// people you follow, and your groups.
    /// </summary>
    /// <param name="sort"><see cref="FeedSorts.Top"/>: most engaging first, paged by <paramref name="skip"/>; otherwise newest first, paged by <paramref name="before"/>.</param>
    /// <param name="imagesOnly">Only posts with photos (a profile's Images).</param>
    public async Task<FeedPageDto> FeedAsync(Guid me, Guid? before, Guid? authorId, Uri site, CancellationToken ct, string? scope = null, Guid? groupId = null,
        string? sort = null, int skip = 0, bool imagesOnly = false)
    {
        var myGroups = db.GroupMembers.Where(m => m.UserId == me).Select(m => m.GroupId);
        var query = Visible(db.Posts.AsNoTracking(), me);
        if (imagesOnly)
            query = query.Where(p => p.Media.Any());
        if (authorId is { } author)
            query = query.Where(p => p.AuthorId == author);
        if (groupId is { } group)
            query = query.Where(p => p.GroupId == group);
        if (scope == FeedScopes.Following)
        {
            var followed = db.Follows.Where(f => f.FollowerId == me).Select(f => f.FolloweeId);
            query = query.Where(p => p.AuthorId == me || followed.Contains(p.AuthorId) || (p.GroupId != null && myGroups.Contains(p.GroupId.Value)));
        }
        IQueryable<Post> ordered;
        if (sort == FeedSorts.Top)
        {
            // Likes count once, comments twice and reposts three times: sharing says the most.
            var since = clock.GetUtcNow().AddDays(-SocialContract.TopWindowDays);
            ordered = query.Where(p => p.CreatedAt >= since)
                .OrderByDescending(p => db.PostLikes.Count(l => l.PostId == p.Id)
                    + 2 * db.PostComments.Count(c => c.PostId == p.Id)
                    + 3 * db.Posts.Count(r => r.RepostOfId == p.Id))
                .ThenByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id)
                .Skip(Math.Max(0, skip));
        }
        else
        {
            if (before is { } anchorId && await db.Posts.Where(p => p.Id == anchorId).Select(p => (DateTimeOffset?)p.CreatedAt).FirstOrDefaultAsync(ct) is { } anchor)
                query = query.Where(p => p.CreatedAt < anchor);
            ordered = query.OrderByDescending(p => p.CreatedAt).ThenByDescending(p => p.Id);
        }

        var page = await Project(ordered.Take(PageSize + 1), me).ToListAsync(ct);
        var originals = await OriginalsAsync(me, page.Take(PageSize), ct);
        return new FeedPageDto(
            page.Take(PageSize).Select(p => ToDto(p, site, originals)).ToList(),
            page.Count > PageSize,
            Nft.Enabled,
            Nft.Enabled ? Nft.Chain : null);
    }

    public async Task<PostDto?> GetAsync(Guid me, Guid postId, Uri site, CancellationToken ct)
    {
        if (await Project(Visible(db.Posts.AsNoTracking(), me).Where(p => p.Id == postId), me).FirstOrDefaultAsync(ct) is not { } row)
            return null;
        return ToDto(row, site, await OriginalsAsync(me, [row], ct));
    }

    /// <summary>Posts this person may see: anything outside a private group, and their own groups' posts.</summary>
    IQueryable<Post> Visible(IQueryable<Post> posts, Guid me) =>
        posts.Where(p => p.GroupId == null || !p.Group!.IsPrivate || db.GroupMembers.Any(m => m.GroupId == p.GroupId && m.UserId == me));

    /// <summary>The originals of the reposts among these rows, as far as this person may see them.</summary>
    async Task<Dictionary<Guid, Row>> OriginalsAsync(Guid me, IEnumerable<Row> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.Post.RepostOfId != null).Select(r => r.Post.RepostOfId!.Value).Distinct().ToList();
        return ids.Count == 0
            ? []
            : await Project(Visible(db.Posts.AsNoTracking(), me).Where(p => ids.Contains(p.Id)), me).ToDictionaryAsync(r => r.Post.Id, ct);
    }

    // ---- Comments ----

    /// <summary>A post's comments, oldest first; null if the post is gone or not for this person.</summary>
    public async Task<List<CommentDto>?> CommentsAsync(Guid me, Guid postId, CancellationToken ct)
    {
        var post = await Visible(db.Posts.AsNoTracking(), me).Where(p => p.Id == postId).Select(p => new { p.AuthorId }).FirstOrDefaultAsync(ct);
        if (post is null)
            return null;
        var comments = await db.PostComments.AsNoTracking().Include(c => c.Author)
            .Where(c => c.PostId == postId)
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Take(SocialContract.MaxComments)
            .ToListAsync(ct);
        return comments.Select(c => ToDto(c, me, post.AuthorId)).ToList();
    }

    public async Task<CommentDto?> AddCommentAsync(User me, Guid postId, string? text, CancellationToken ct)
    {
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            throw new ChatRejectedException("Write a comment first.");
        if (text.Length > SocialContract.MaxCommentLength)
            throw new ChatRejectedException($"Comments are limited to {SocialContract.MaxCommentLength} characters.");
        var post = await Visible(db.Posts, me.Id).Where(p => p.Id == postId).Select(p => new { p.AuthorId }).FirstOrDefaultAsync(ct);
        if (post is null)
            return null;

        var comment = new PostComment { Id = Guid.NewGuid(), PostId = postId, AuthorId = me.Id, Author = me, Text = text, CreatedAt = clock.GetUtcNow() };
        db.PostComments.Add(comment);
        await db.SaveChangesAsync(ct);
        return ToDto(comment, me.Id, post.AuthorId);
    }

    /// <summary>By the person who wrote it, or the post's author (their post, their comments section).</summary>
    public async Task<bool> DeleteCommentAsync(Guid me, Guid postId, Guid commentId, CancellationToken ct)
    {
        var comment = await db.PostComments.FirstOrDefaultAsync(c => c.Id == commentId && c.PostId == postId, ct);
        if (comment is null)
            return false;
        if (comment.AuthorId != me && !await db.Posts.AnyAsync(p => p.Id == postId && p.AuthorId == me, ct))
            throw new ChatRejectedException("You can only delete your own comments, or comments on your posts.");
        db.PostComments.Remove(comment);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>What someone has commented, newest first, on posts this person may see: their profile's Activity.</summary>
    public async Task<List<CommentActivityDto>> CommentActivityAsync(Guid me, Guid authorId, Uri site, CancellationToken ct)
    {
        var visible = Visible(db.Posts, me).Select(p => p.Id);
        var comments = await db.PostComments.AsNoTracking().Include(c => c.Author)
            .Where(c => c.AuthorId == authorId && visible.Contains(c.PostId))
            .OrderByDescending(c => c.CreatedAt)
            .Take(PageSize)
            .ToListAsync(ct);
        var postIds = comments.Select(c => c.PostId).Distinct().ToList();
        var rows = await Project(db.Posts.AsNoTracking().Where(p => postIds.Contains(p.Id)), me).ToListAsync(ct);
        var originals = await OriginalsAsync(me, rows, ct);
        var posts = rows.ToDictionary(r => r.Post.Id, r => ToDto(r, site, originals));
        return comments
            .Where(c => posts.ContainsKey(c.PostId))
            .Select(c => new CommentActivityDto(ToDto(c, me, posts[c.PostId].Author.Id), posts[c.PostId]))
            .ToList();
    }

    static CommentDto ToDto(PostComment comment, Guid me, Guid postAuthorId) =>
        new(comment.Id, comment.PostId, ChatMapper.ToDto(comment.Author), comment.Text, comment.CreatedAt, comment.AuthorId == me || postAuthorId == me);

    // ---- Reposts ----

    /// <summary>
    /// Shares a post to your followers, with your thoughts if you like. Reposting a repost shares
    /// its original. Reposting again changes your thoughts. Private groups' posts stay in the group.
    /// </summary>
    public async Task<PostDto?> RepostAsync(User me, Guid postId, string? caption, Uri site, CancellationToken ct)
    {
        caption = string.IsNullOrWhiteSpace(caption) ? null : caption.Trim();
        if (caption?.Length > SocialContract.MaxCaptionLength)
            throw new ChatRejectedException($"Captions are limited to {SocialContract.MaxCaptionLength} characters.");
        var post = await Visible(db.Posts, me.Id).Include(p => p.Group).FirstOrDefaultAsync(p => p.Id == postId, ct);
        if (post is null)
            return null;
        if (post.RepostOfId is { } originalId)
        {
            post = await Visible(db.Posts, me.Id).Include(p => p.Group).FirstOrDefaultAsync(p => p.Id == originalId, ct);
            if (post is null)
                return null;
        }
        if (post.Group is { IsPrivate: true })
            throw new ChatRejectedException("Posts in private groups can't be reposted.");

        var repost = await db.Posts.FirstOrDefaultAsync(p => p.AuthorId == me.Id && p.RepostOfId == post.Id, ct);
        if (repost is null)
        {
            repost = new Post { Id = Guid.NewGuid(), AuthorId = me.Id, RepostOfId = post.Id, Caption = caption, CreatedAt = clock.GetUtcNow() };
            db.Posts.Add(repost);
        }
        else
        {
            repost.Caption = caption;
        }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Reposted twice at once: the first one stands.
            db.ChangeTracker.Clear();
            repost = await db.Posts.FirstAsync(p => p.AuthorId == me.Id && p.RepostOfId == post.Id, ct);
        }
        return await GetAsync(me.Id, repost.Id, site, ct);
    }

    /// <summary>Takes back your repost of a post (or of a repost's original); returns the original.</summary>
    public async Task<PostDto?> UndoRepostAsync(User me, Guid postId, Uri site, CancellationToken ct)
    {
        var originalId = await db.Posts.Where(p => p.Id == postId).Select(p => p.RepostOfId ?? p.Id).FirstOrDefaultAsync(ct);
        if (originalId == Guid.Empty)
            return null;
        await db.Posts.Where(p => p.AuthorId == me.Id && p.RepostOfId == originalId).ExecuteDeleteAsync(ct);
        return await GetAsync(me.Id, originalId, site, ct);
    }

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
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Its reposts go with it (their likes and comments cascade).
        await db.Posts.Where(p => p.RepostOfId == postId).ExecuteDeleteAsync(ct);
        db.Posts.Remove(post);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
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

    sealed record Row(Post Post, User Author, List<PostMedia> Media, int Likes, bool Liked, TokenTransfer? Mint, string? GroupName,
        int Comments, int Reposts, bool Reposted);

    IQueryable<Row> Project(IQueryable<Post> posts, Guid me) => posts.Select(p => new Row(
        p,
        p.Author,
        p.Media.OrderBy(m => m.Position).ToList(),
        db.PostLikes.Count(l => l.PostId == p.Id),
        db.PostLikes.Any(l => l.PostId == p.Id && l.UserId == me),
        p.Mint,
        p.Group == null ? null : p.Group.Name,
        db.PostComments.Count(c => c.PostId == p.Id),
        db.Posts.Count(r => r.RepostOfId == p.Id),
        db.Posts.Any(r => r.RepostOfId == p.Id && r.AuthorId == me)));

    static PostDto ToDto(Row row, Uri site, IReadOnlyDictionary<Guid, Row>? originals = null) => new(
        row.Post.Id,
        ChatMapper.ToDto(row.Author),
        row.Post.Caption,
        row.Media.Select(m => new PostMediaDto(m.Id, m.Width, m.Height, Absolute(site, SocialContract.MediaPath(m.Id, PhotoSizes.Feed)))).ToList(),
        row.Post.CreatedAt,
        row.Likes,
        row.Liked,
        row.Mint is null ? null : ToNftDto(row.Mint),
        row.Post.GroupId is { } groupId ? new GroupRefDto(groupId, row.GroupName!) : null,
        row.Comments,
        row.Reposts,
        row.Reposted,
        row.Post.RepostOfId is { } originalId && originals?.GetValueOrDefault(originalId) is { } original ? ToDto(original, site) : null,
        row.Post.RepostOfId is not null);

    public static PostNftDto ToNftDto(TokenTransfer mint) => new(
        mint.PostId!.Value, mint.Status, mint.Chain, mint.TokenStandard, mint.ContractAddress, mint.TokenId!, mint.MetadataUri!, mint.TxHash, mint.Error);

    static string Absolute(Uri site, string path) => new Uri(site, path).ToString();

    static string ShortId(Guid id) => id.ToString("N")[..8].ToUpperInvariant();
}
