using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>
/// A feed of posts: everyone's, or one person's, one group's, or the people you follow. Sorted by
/// Recent or Top. Posts can be liked, commented on and reposted; your own can be minted as NFTs (or
/// deleted, until they are), and a mint's progress arrives live.
/// </summary>
public sealed partial class FeedViewModel : ObservableObject
{
    readonly ChatApi _api;
    readonly ChatSession _session;
    readonly INavigator _navigator;
    readonly IDialogs _dialogs;
    readonly TimeProvider _clock;

    public FeedViewModel(ChatApi api, ChatSession session, INavigator navigator, IDialogs dialogs, IUiDispatcher ui, TimeProvider clock)
    {
        (_api, _session, _navigator, _dialogs, _clock) = (api, session, navigator, dialogs, clock);
        _session.Connection.PostNftChanged += nft => ui.Post(() =>
        {
            foreach (var post in Showing(nft.PostId))
                post.Update(nft);
        });
    }

    public ObservableCollection<PostItemViewModel> Posts { get; } = [];

    [ObservableProperty]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingMore { get; set; }

    [ObservableProperty]
    public partial bool HasMore { get; set; }

    /// <summary>Whether the server can mint; if not, the mint option isn't offered.</summary>
    [ObservableProperty]
    public partial bool MintingEnabled { get; set; }

    [ObservableProperty]
    public partial string? NftChain { get; set; }

    /// <summary><see cref="FeedSorts.Recent"/> or <see cref="FeedSorts.Top"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortText), nameof(IsTop))]
    public partial string Sort { get; set; } = FeedSorts.Recent;

    public bool IsTop => Sort == FeedSorts.Top;
    public string SortText => IsTop ? "Top" : "Recent";

    public bool IsEmpty => Posts.Count == 0;

    /// <summary>Which posts: "following" (see FeedScopes), and/or one person's or one group's. The shared feed leaves them null.</summary>
    public string? Scope { get; set; }
    public Guid? AuthorId { get; set; }
    public Guid? GroupId { get; set; }

    /// <summary>Only posts with photos (a profile's Images).</summary>
    public bool ImagesOnly { get; set; }

    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            var page = await _api.GetFeedAsync(author: AuthorId, scope: Scope, group: GroupId, sort: Sort, imagesOnly: ImagesOnly);
            Apply(page);
            Posts.Clear();
            foreach (var post in page.Posts)
                Posts.Add(Item(post));
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't load the feed", ex.Message);
        }
        finally
        {
            IsRefreshing = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    [RelayCommand]
    async Task LoadMoreAsync()
    {
        if (!HasMore || IsLoadingMore || Posts.Count == 0)
            return;
        IsLoadingMore = true;
        try
        {
            // Top is ranked, so it pages by position; Recent by the last post shown.
            var page = IsTop
                ? await _api.GetFeedAsync(author: AuthorId, scope: Scope, group: GroupId, sort: Sort, skip: Posts.Count, imagesOnly: ImagesOnly)
                : await _api.GetFeedAsync(before: Posts[^1].Id, author: AuthorId, scope: Scope, group: GroupId, sort: Sort, imagesOnly: ImagesOnly);
            Apply(page);
            foreach (var post in page.Posts.Where(p => Posts.All(q => q.Id != p.Id)))
                Posts.Add(Item(post));
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't load more posts", ex.Message);
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    async Task SetSortAsync(string sort)
    {
        if (sort == Sort)
            return;
        Sort = sort;
        await RefreshAsync();
    }

    [RelayCommand]
    Task ComposeAsync() => _navigator.GoToAsync(Routes.ComposePost);

    /// <summary>Shows at once, then settles on the server's count; undone if the server refuses.</summary>
    [RelayCommand]
    async Task ToggleLikeAsync(PostItemViewModel post)
    {
        var like = !post.LikedByMe;
        var count = post.LikeCount + (like ? 1 : -1);
        foreach (var item in Showing(post.TargetId))
            item.SetLike(like, count);
        try
        {
            var result = await _api.SetPostLikeAsync(post.TargetId, like);
            foreach (var item in Showing(post.TargetId))
                item.SetLike(result.LikedByMe, result.LikeCount);
        }
        catch (ApiException ex)
        {
            foreach (var item in Showing(post.TargetId))
                item.SetLike(!like, count + (like ? -1 : 1));
            await _dialogs.AlertAsync("Couldn't update the like", ex.Message);
        }
    }

    // ---- Comments ----

    /// <summary>Opens (loading them the first time) or closes a post's comments.</summary>
    [RelayCommand]
    async Task ToggleCommentsAsync(PostItemViewModel post)
    {
        post.IsCommentsOpen = !post.IsCommentsOpen;
        if (post.IsCommentsOpen && !post.CommentsLoaded)
            await LoadCommentsAsync(post);
    }

    public async Task LoadCommentsAsync(PostItemViewModel post)
    {
        post.IsLoadingComments = true;
        try
        {
            var comments = await _api.GetCommentsAsync(post.TargetId);
            post.Comments.Clear();
            foreach (var comment in comments)
                post.Comments.Add(new CommentItemViewModel(comment, _clock));
            post.CommentsLoaded = true;
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't load the comments", ex.Message);
        }
        finally
        {
            post.IsLoadingComments = false;
        }
    }

    [RelayCommand]
    async Task AddCommentAsync(PostItemViewModel post)
    {
        var text = post.CommentDraft?.Trim();
        if (string.IsNullOrEmpty(text) || post.IsSendingComment)
            return;
        post.IsSendingComment = true;
        try
        {
            var comment = await _api.AddCommentAsync(post.TargetId, text);
            post.CommentDraft = "";
            foreach (var item in Showing(post.TargetId))
            {
                if (item.CommentsLoaded)
                    item.Comments.Add(new CommentItemViewModel(comment, _clock));
                item.CommentCount++;
            }
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't add the comment", ex.Message);
        }
        finally
        {
            post.IsSendingComment = false;
        }
    }

    [RelayCommand]
    async Task DeleteCommentAsync(CommentItemViewModel comment)
    {
        try
        {
            await _api.DeleteCommentAsync(comment.PostId, comment.Id);
            foreach (var item in Showing(comment.PostId))
            {
                if (item.Comments.FirstOrDefault(c => c.Id == comment.Id) is { } shown)
                    item.Comments.Remove(shown);
                item.CommentCount = Math.Max(0, item.CommentCount - 1);
            }
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't delete the comment", ex.Message);
        }
    }

    // ---- Reposts ----

    /// <summary>Reposts it straight away, or takes your repost back if you already have.</summary>
    [RelayCommand]
    Task RepostAsync(PostItemViewModel post) => post.RepostedByMe ? UndoRepostAsync(post) : RepostWithThoughtsAsync(post, null);

    /// <summary>Reposts with your own thoughts on top (or none); the repost joins the top of the feed.</summary>
    public async Task RepostWithThoughtsAsync(PostItemViewModel post, string? thoughts)
    {
        try
        {
            var repost = await _api.RepostAsync(post.TargetId, string.IsNullOrWhiteSpace(thoughts) ? null : thoughts.Trim());
            // Reposting again replaces the earlier one.
            foreach (var mine in Posts.Where(p => p.Post.IsRepost && p.IsMine && p.Post.RepostOf?.Id == post.TargetId).ToList())
                Posts.Remove(mine);
            if (repost.RepostOf is { } original)
                foreach (var item in Showing(original.Id))
                    item.SetReposts(true, original.RepostCount);
            if (AuthorId is null || AuthorId == _session.MyUserId)
                Prepend(repost);
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't repost", ex.Message);
        }
    }

    async Task UndoRepostAsync(PostItemViewModel post)
    {
        try
        {
            var original = await _api.UndoRepostAsync(post.TargetId);
            foreach (var mine in Posts.Where(p => p.Post.IsRepost && p.IsMine && p.Post.RepostOf?.Id == original.Id).ToList())
                Posts.Remove(mine);
            foreach (var item in Showing(original.Id))
                item.SetReposts(false, original.RepostCount);
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't undo the repost", ex.Message);
        }
    }

    [RelayCommand]
    async Task MintAsync(PostItemViewModel post)
    {
        if (!post.CanMint)
            return;
        post.IsBusy = true;
        try
        {
            var minted = await _api.MintPostAsync(post.Id);
            if (minted.Nft is { } nft)
                post.Update(nft);
        }
        catch (ApiException ex)
        {
            await _dialogs.AlertAsync("Couldn't mint the post", ex.Message);
        }
        finally
        {
            post.IsBusy = false;
        }
    }

    [RelayCommand]
    async Task DeleteAsync(PostItemViewModel post)
    {
        if (!post.CanDelete)
            return;
        post.IsBusy = true;
        try
        {
            await _api.DeletePostAsync(post.Id);
            // Its reposts go with it; a repost of mine going frees the original's repost button.
            foreach (var gone in Posts.Where(p => p.Id == post.Id || p.Post.RepostOf?.Id == post.Id).ToList())
                Posts.Remove(gone);
            if (post.Post.IsRepost && post.Post.RepostOf is { } original)
                foreach (var item in Showing(original.Id))
                    item.SetReposts(false, Math.Max(0, item.RepostCount - 1));
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (ApiException ex)
        {
            post.IsBusy = false;
            await _dialogs.AlertAsync("Couldn't delete the post", ex.Message);
        }
    }

    /// <summary>A post just published on this device goes to the top without a reload.</summary>
    public void Prepend(PostDto post)
    {
        if (Posts.Any(p => p.Id == post.Id))
            return;
        Posts.Insert(0, Item(post));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Every card showing this post: itself, and plain reposts of it.</summary>
    IEnumerable<PostItemViewModel> Showing(Guid postId) => Posts.Where(p => p.TargetId == postId).ToList();

    PostItemViewModel Item(PostDto post) => new(post, _session.MyUserId, MintingEnabled, _clock);

    void Apply(FeedPageDto page) => (HasMore, MintingEnabled, NftChain) = (page.HasMore, page.MintingEnabled, page.NftChain);
}

/// <summary>
/// One card in a feed. A plain repost shows the original (with "X reposted this" above it), and
/// likes, comments and reposts go to the original. A repost with the reposter's thoughts is a post of
/// its own: their words, with the original inside it.
/// </summary>
public sealed partial class PostItemViewModel : ObservableObject
{
    readonly bool _mintingEnabled;
    readonly TimeProvider _clock;

    public PostItemViewModel(PostDto post, Guid myUserId, bool mintingEnabled, TimeProvider clock)
    {
        Post = post;
        _mintingEnabled = mintingEnabled;
        _clock = clock;
        IsMine = post.Author.Id == myUserId;
        Shown = post is { IsRepost: true, Caption: null, RepostOf: { } original } ? original : post;
        TimeText = Display.ListTime(Shown.CreatedAt, clock.GetUtcNow());
        LikeCount = Shown.LikeCount;
        LikedByMe = Shown.LikedByMe;
        CommentCount = Shown.CommentCount;
        RepostCount = Shown.RepostCount;
        RepostedByMe = Shown.RepostedByMe;
        Nft = Shown.Nft;
    }

    /// <summary>The post this card is for (possibly a repost).</summary>
    public PostDto Post { get; }

    /// <summary>What the card shows: the post, or for a plain repost, its original.</summary>
    public PostDto Shown { get; }

    public Guid Id => Post.Id;

    /// <summary>What likes, comments and reposts on this card go to.</summary>
    public Guid TargetId => Shown.Id;

    public bool IsPlainRepost => !ReferenceEquals(Shown, Post);
    public string? RepostedByText => IsPlainRepost ? $"{Post.Author.DisplayName} reposted this" : null;

    public Guid AuthorId => Shown.Author.Id;
    public string AuthorName => Shown.Author.DisplayName;
    public string AuthorInitials => Display.Initials(Shown.Author.DisplayName);
    public string? AuthorAvatarUrl => Shown.Author.AvatarUrl;
    public string? Caption => Shown.Caption;
    public bool HasCaption => Shown.Caption is not null;
    public IReadOnlyList<PostMediaDto> Photos => Shown.Media;
    public bool HasPhotos => Shown.Media.Count > 0;
    public bool HasSeveralPhotos => Shown.Media.Count > 1;
    public GroupRefDto? Group => Shown.Group;
    public bool IsMine { get; }
    public string TimeText { get; }

    /// <summary>A repost with thoughts: the original inside it.</summary>
    public PostDto? Embedded => !IsPlainRepost && Post.IsRepost ? Post.RepostOf : null;
    public bool HasEmbedded => Embedded is not null;
    public bool IsEmbeddedMissing => Post.IsRepost && Post.RepostOf is null;
    public string EmbeddedAuthorName => Embedded?.Author.DisplayName ?? "";
    public string EmbeddedAuthorInitials => Display.Initials(Embedded?.Author.DisplayName);
    public string? EmbeddedAuthorAvatarUrl => Embedded?.Author.AvatarUrl;
    public string? EmbeddedCaption => Embedded?.Caption;
    public bool HasEmbeddedCaption => Embedded?.Caption is not null;
    public IReadOnlyList<PostMediaDto> EmbeddedPhotos => Embedded?.Media ?? [];
    public bool HasEmbeddedPhotos => EmbeddedPhotos.Count > 0;
    public string EmbeddedTimeText => Embedded is { } e ? Display.ListTime(e.CreatedAt, _clock.GetUtcNow()) : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeText), nameof(StatsText))]
    public partial int LikeCount { get; set; }

    [ObservableProperty]
    public partial bool LikedByMe { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommentText), nameof(StatsText))]
    public partial int CommentCount { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepostText), nameof(StatsText))]
    public partial int RepostCount { get; set; }

    [ObservableProperty]
    public partial bool RepostedByMe { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNft), nameof(IsMinted), nameof(NftText), nameof(CanMint), nameof(MintText), nameof(CanDelete))]
    public partial PostNftDto? Nft { get; set; }

    /// <summary>A mint or delete is on its way to the server.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public ObservableCollection<CommentItemViewModel> Comments { get; } = [];

    [ObservableProperty]
    public partial bool IsCommentsOpen { get; set; }

    [ObservableProperty]
    public partial bool IsLoadingComments { get; set; }

    public bool CommentsLoaded { get; set; }

    [ObservableProperty]
    public partial string? CommentDraft { get; set; }

    [ObservableProperty]
    public partial bool IsSendingComment { get; set; }

    public string LikeText => LikeCount == 1 ? "1 like" : $"{LikeCount} likes";
    public string CommentText => CommentCount == 1 ? "1 comment" : $"{CommentCount} comments";
    public string RepostText => RepostCount == 1 ? "1 repost" : $"{RepostCount} reposts";

    /// <summary>"3 likes · 2 comments · 1 repost", leaving out what's zero.</summary>
    public string StatsText => string.Join(" · ", new[] { (LikeCount, LikeText), (CommentCount, CommentText), (RepostCount, RepostText) }
        .Where(s => s.Item1 > 0).Select(s => s.Item2));

    public bool HasNft => Nft is not null;
    public bool IsMinted => Nft?.Status == TransferStatuses.Confirmed;

    public string NftText => Nft switch
    {
        null => "",
        { Status: TransferStatuses.Pending } => "Queued for minting",
        { Status: TransferStatuses.Processing } => "Minting on-chain…",
        { Status: TransferStatuses.Confirmed } n => $"NFT #{ShortTokenId(n.TokenId)} on {n.Chain}" + (n.TxHash is { } tx ? $" · {Display.ShortAddress(tx)}" : ""),
        { Status: TransferStatuses.Failed } n => $"Minting failed: {n.Error ?? "unknown error"}",
        var n => n.Status
    };

    /// <summary>Your own post (not a repost), not minted or being minted -- or whose mint failed -- on a server that mints.</summary>
    public bool CanMint => IsMine && !Post.IsRepost && _mintingEnabled && Post.Media.Count > 0 && (Nft is null || Nft.Status == TransferStatuses.Failed);

    public string MintText => Nft?.Status == TransferStatuses.Failed ? "Retry mint" : "Mint as NFT";

    /// <summary>Your own posts and reposts; minted posts stay, as their NFT shows these photos.</summary>
    public bool CanDelete => IsMine && (Post.IsRepost || Nft is null || Nft.Status == TransferStatuses.Failed);

    public string DeleteText => IsPlainRepost ? "Undo repost" : "Delete";

    public void SetLike(bool liked, int count) => (LikedByMe, LikeCount) = (liked, Math.Max(0, count));

    public void SetReposts(bool reposted, int count) => (RepostedByMe, RepostCount) = (reposted, Math.Max(0, count));

    public void Update(PostNftDto nft) => Nft = nft;

    /// <summary>Token ids are 128-bit numbers; the last eight digits tell them apart.</summary>
    static string ShortTokenId(string tokenId) => tokenId.Length > 8 ? "…" + tokenId[^8..] : tokenId;
}

public sealed class CommentItemViewModel(CommentDto comment, TimeProvider clock)
{
    public CommentDto Comment => comment;
    public Guid Id => comment.Id;
    public Guid PostId => comment.PostId;
    public Guid AuthorId => comment.Author.Id;
    public string AuthorName => comment.Author.DisplayName;
    public string AuthorInitials => Display.Initials(comment.Author.DisplayName);
    public string? AuthorAvatarUrl => comment.Author.AvatarUrl;
    public string Text => comment.Text;
    public string TimeText => Display.ListTime(comment.CreatedAt, clock.GetUtcNow());
    public bool CanDelete => comment.CanDelete;
}

/// <summary>A new post: up to four photos, a caption, and whether to mint it straight away.</summary>
public sealed partial class ComposePostViewModel : ObservableObject
{
    readonly ChatApi _api;
    readonly FeedViewModel _feed;
    readonly INavigator _navigator;

    public ComposePostViewModel(ChatApi api, FeedViewModel feed, INavigator navigator)
    {
        (_api, _feed, _navigator) = (api, feed, navigator);
        Caption = "";
    }

    public ObservableCollection<ComposePhoto> Photos { get; } = [];

    [ObservableProperty]
    public partial string Caption { get; set; }

    [ObservableProperty]
    public partial bool MintAsNft { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PublishCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? ErrorMessage { get; set; }

    public bool MintingEnabled => _feed.MintingEnabled;
    public string MintLabel => _feed.NftChain is { } chain ? $"Mint as an NFT on {chain}" : "Mint as an NFT";
    public bool CanAddPhoto => Photos.Count < SocialContract.MaxPhotos;
    public int CaptionRemaining => SocialContract.MaxCaptionLength - Caption.Length;

    partial void OnCaptionChanged(string value) => OnPropertyChanged(nameof(CaptionRemaining));

    /// <summary>A photo from the camera or the gallery. Returns false, with a reason in ErrorMessage, if it can't be added.</summary>
    public bool AddPhoto(byte[] photo)
    {
        ErrorMessage = null;
        if (!CanAddPhoto)
            ErrorMessage = $"A post can have up to {SocialContract.MaxPhotos} photos.";
        else if (photo.Length > SocialContract.MaxPhotoBytes)
            ErrorMessage = $"That photo is larger than {SocialContract.MaxPhotoBytes / (1024 * 1024)} MB.";
        else if (ImageDimensions.Read(photo) is not { } size)
            ErrorMessage = "That isn't a JPEG or PNG photo.";
        else if (Math.Min(size.Width, size.Height) < SocialContract.MinPhotoShortSide)
            ErrorMessage = $"That photo is too small: it needs at least {SocialContract.MinPhotoShortSide} pixels on each side.";
        else
        {
            Photos.Add(new ComposePhoto(photo));
            OnPropertyChanged(nameof(CanAddPhoto));
            return true;
        }
        return false;
    }

    [RelayCommand]
    void RemovePhoto(ComposePhoto photo)
    {
        Photos.Remove(photo);
        OnPropertyChanged(nameof(CanAddPhoto));
    }

    /// <summary>Post in this group (the web's group page sets it); null for your own timeline.</summary>
    public Guid? GroupId { get; set; }

    [RelayCommand(CanExecute = nameof(CanPublish))]
    async Task PublishAsync()
    {
        ErrorMessage = null;
        if (Photos.Count == 0 && string.IsNullOrWhiteSpace(Caption))
        {
            ErrorMessage = "Write something or add a photo.";
            return;
        }
        if (CaptionRemaining < 0)
        {
            ErrorMessage = $"Captions are limited to {SocialContract.MaxCaptionLength} characters.";
            return;
        }

        IsBusy = true;
        try
        {
            var post = await _api.CreatePostAsync(Caption.Trim(), Photos.Select(p => p.Data).ToList(), MintAsNft && MintingEnabled && Photos.Count > 0, GroupId);
            _feed.Prepend(post);
            Photos.Clear();
            (Caption, MintAsNft) = ("", false);
            OnPropertyChanged(nameof(CanAddPhoto));
            await _navigator.GoBackAsync();
        }
        catch (ApiException ex)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    bool CanPublish() => !IsBusy;
}

public sealed class ComposePhoto(byte[] data)
{
    public Guid Id { get; } = Guid.NewGuid();
    public byte[] Data => data;
}
