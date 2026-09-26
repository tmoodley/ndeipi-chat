namespace NdeipiChat.Contracts;

/// <summary>
/// The feed: public posts of one to four photos. An author can mint a post as an ERC-721 NFT;
/// Ndeipi Enterprise Server mints it from the same queue as token transfers.
/// </summary>
public static class SocialContract
{
    public const string PostsPath = "/api/posts";
    public const int MaxPhotos = 4;
    public const int MaxCaptionLength = 2200;

    /// <summary>Per photo, as uploaded. The server re-encodes it, dropping EXIF (and so any GPS position).</summary>
    public const int MaxPhotoBytes = 10 * 1024 * 1024;

    public const int MinPhotoShortSide = 200;

    /// <summary>Public, no sign-in: a photo, at <see cref="PhotoSizes"/>.</summary>
    public static string MediaPath(Guid mediaId, string size) => $"media/posts/{mediaId:N}/{size}";

    /// <summary>Public, no sign-in: a minted post's ERC-721 metadata, the NFT's tokenURI.</summary>
    public static string MetadataPath(Guid postId) => $"nft/posts/{postId:N}";
}

public static class PhotoSizes
{
    public const string Thumb = "thumb";
    public const string Feed = "feed";
    public const string Full = "full";

    /// <summary>The longest edge of each size, in pixels.</summary>
    public static readonly IReadOnlyDictionary<string, int> MaxEdge = new Dictionary<string, int>
    {
        [Thumb] = 320,
        [Feed] = 1080,
        [Full] = 2048
    };
}

/// <param name="Url">Absolute, public; ends in "/feed". Swap for <see cref="PhotoSizes"/> as needed.</param>
public sealed record PostMediaDto(Guid Id, int Width, int Height, string Url);

/// <summary>
/// A post's NFT. <see cref="Status"/> follows the queue: Pending, Processing, Confirmed, Failed
/// (<see cref="TransferStatuses"/>). <see cref="TokenId"/> is fixed when the mint is queued.
/// </summary>
public sealed record PostNftDto(
    Guid PostId,
    string Status,
    string Chain,
    string Standard,
    string? ContractAddress,
    string TokenId,
    string MetadataUrl,
    string? TxHash,
    string? Error);

public sealed record PostDto(
    Guid Id,
    UserDto Author,
    string? Caption,
    IReadOnlyList<PostMediaDto> Media,
    DateTimeOffset CreatedAt,
    int LikeCount,
    bool LikedByMe,
    PostNftDto? Nft);

/// <param name="MintingEnabled">Whether the server has an NFT contract set up; if not, the mint option is hidden.</param>
public sealed record FeedPageDto(IReadOnlyList<PostDto> Posts, bool HasMore, bool MintingEnabled, string? NftChain);

public sealed record LikeResultDto(Guid PostId, int LikeCount, bool LikedByMe);
