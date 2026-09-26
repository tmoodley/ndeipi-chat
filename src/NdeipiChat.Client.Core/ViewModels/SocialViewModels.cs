using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.ViewModels;

/// <summary>
/// The Feed tab: everyone's posts, newest first, with likes. Your own posts can be minted as NFTs
/// (or deleted, until they are); a mint's progress arrives live.
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
        _session.Connection.PostNftChanged += nft => ui.Post(() => Find(nft.PostId)?.Update(nft));
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

    public bool IsEmpty => Posts.Count == 0;

    [RelayCommand]
    async Task RefreshAsync()
    {
        try
        {
            var page = await _api.GetFeedAsync();
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
            var page = await _api.GetFeedAsync(before: Posts[^1].Id);
            Apply(page);
            foreach (var post in page.Posts.Where(p => Find(p.Id) is null))
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
    Task ComposeAsync() => _navigator.GoToAsync(Routes.ComposePost);

    /// <summary>Shows at once, then settles on the server's count; undone if the server refuses.</summary>
    [RelayCommand]
    async Task ToggleLikeAsync(PostItemViewModel post)
    {
        var like = !post.LikedByMe;
        post.SetLike(like, post.LikeCount + (like ? 1 : -1));
        try
        {
            var result = await _api.SetPostLikeAsync(post.Id, like);
            post.SetLike(result.LikedByMe, result.LikeCount);
        }
        catch (ApiException ex)
        {
            post.SetLike(!like, post.LikeCount + (like ? -1 : 1));
            await _dialogs.AlertAsync("Couldn't update the like", ex.Message);
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
            Posts.Remove(post);
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
        if (Find(post.Id) is not null)
            return;
        Posts.Insert(0, Item(post));
        OnPropertyChanged(nameof(IsEmpty));
    }

    PostItemViewModel? Find(Guid id) => Posts.FirstOrDefault(p => p.Id == id);

    PostItemViewModel Item(PostDto post) => new(post, _session.MyUserId, MintingEnabled, _clock);

    void Apply(FeedPageDto page) => (HasMore, MintingEnabled, NftChain) = (page.HasMore, page.MintingEnabled, page.NftChain);
}

public sealed partial class PostItemViewModel : ObservableObject
{
    readonly bool _mintingEnabled;

    public PostItemViewModel(PostDto post, Guid myUserId, bool mintingEnabled, TimeProvider clock)
    {
        Post = post;
        _mintingEnabled = mintingEnabled;
        IsMine = post.Author.Id == myUserId;
        TimeText = Display.ListTime(post.CreatedAt, clock.GetUtcNow());
        LikeCount = post.LikeCount;
        LikedByMe = post.LikedByMe;
        Nft = post.Nft;
    }

    public PostDto Post { get; }
    public Guid Id => Post.Id;
    public string AuthorName => Post.Author.DisplayName;
    public string AuthorInitials => Display.Initials(Post.Author.DisplayName);
    public string? AuthorAvatarUrl => Post.Author.AvatarUrl;
    public string? Caption => Post.Caption;
    public bool HasCaption => Post.Caption is not null;
    public IReadOnlyList<PostMediaDto> Photos => Post.Media;
    public bool HasSeveralPhotos => Post.Media.Count > 1;
    public bool IsMine { get; }
    public string TimeText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LikeText))]
    public partial int LikeCount { get; set; }

    [ObservableProperty]
    public partial bool LikedByMe { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNft), nameof(IsMinted), nameof(NftText), nameof(CanMint), nameof(MintText), nameof(CanDelete))]
    public partial PostNftDto? Nft { get; set; }

    /// <summary>A mint or delete is on its way to the server.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public string LikeText => LikeCount == 1 ? "1 like" : $"{LikeCount} likes";
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

    /// <summary>Your own post, not minted or being minted -- or whose mint failed -- on a server that mints.</summary>
    public bool CanMint => IsMine && _mintingEnabled && (Nft is null || Nft.Status == TransferStatuses.Failed);

    public string MintText => Nft?.Status == TransferStatuses.Failed ? "Retry mint" : "Mint as NFT";

    /// <summary>Minted posts stay: their NFT shows these photos.</summary>
    public bool CanDelete => IsMine && (Nft is null || Nft.Status == TransferStatuses.Failed);

    public void SetLike(bool liked, int count) => (LikedByMe, LikeCount) = (liked, Math.Max(0, count));

    public void Update(PostNftDto nft) => Nft = nft;

    /// <summary>Token ids are 128-bit numbers; the last eight digits tell them apart.</summary>
    static string ShortTokenId(string tokenId) => tokenId.Length > 8 ? "…" + tokenId[^8..] : tokenId;
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

    [RelayCommand(CanExecute = nameof(CanPublish))]
    async Task PublishAsync()
    {
        ErrorMessage = null;
        if (Photos.Count == 0)
        {
            ErrorMessage = "Add at least one photo.";
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
            var post = await _api.CreatePostAsync(Caption.Trim(), Photos.Select(p => p.Data).ToList(), MintAsNft && MintingEnabled);
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
