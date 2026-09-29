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
    PostNftDto? Nft,
    GroupRefDto? Group = null);

/// <param name="MintingEnabled">Whether the server has an NFT contract set up; if not, the mint option is hidden.</param>
public sealed record FeedPageDto(IReadOnlyList<PostDto> Posts, bool HasMore, bool MintingEnabled, string? NftChain);

public sealed record LikeResultDto(Guid PostId, int LikeCount, bool LikedByMe);

/// <summary>
/// The social graph around posts: profiles, follows and groups (the Social app). Follows are
/// one-way and public; Shamwaris stay the two-way, chat-with-me relationship.
/// </summary>
public static class SocialGraphContract
{
    public const string BasePath = "api/social";
    public const int MaxBio = 500;
    public const int MaxGroupName = 80;
    public const int MaxGroupDescription = 1000;
    public const int MaxGroupRules = 2000;

    /// <summary>Icon colours a group can pick, matching the shell's tile colours.</summary>
    public static readonly IReadOnlyList<string> GroupTones = ["blue", "green", "red", "yellow", "purple", "brown", "teal", "orange", "pink"];
}

public static class GroupRoles
{
    public const string Owner = "owner";
    public const string Member = "member";
}

public sealed record GroupRefDto(Guid Id, string Name);

/// <param name="Gig">Their Gigs work profile, if they have one: what they do, and how they're rated.</param>
public sealed record SocialProfileDto(
    UserDto User,
    string? Bio,
    string? City,
    string? Website,
    DateTimeOffset JoinedAt,
    int PostCount,
    int FollowerCount,
    int FollowingCount,
    int GroupCount,
    bool IsMe,
    bool IFollow,
    bool FollowsMe,
    bool IsShamwari,
    IReadOnlyList<GroupSummaryDto> Groups,
    IReadOnlyList<PostMediaDto> Photos,
    GigProfileBriefDto? Gig,
    string? CoverUrl = null,
    ProfileDetailsDto? Details = null,
    int Completion = 0,
    bool HasCustomAvatar = false);

public sealed record GigProfileBriefDto(string Headline, IReadOnlyList<string> Skills, double? Rating, int RatingCount, int CompletedGigs, bool IsAvailable);

public sealed record SaveSocialProfileRequest(string? Bio, string? City, string? Website);

/// <summary>
/// The rest of a profile, edited as a whole: what they do, where they're from, what they're into,
/// their work and study history, skills and links elsewhere.
/// </summary>
public sealed record ProfileDetailsDto(
    string? Occupation,
    string? Country,
    IReadOnlyList<string> Skills,
    IReadOnlyList<InterestDto> Interests,
    IReadOnlyList<TimelineEntryDto> Jobs,
    IReadOnlyList<TimelineEntryDto> Education,
    IReadOnlyList<SocialLinkDto> Links)
{
    public static ProfileDetailsDto Empty { get; } = new(null, null, [], [], [], [], []);
}

/// <summary>A heading and a line of things, e.g. "Favourite music" and "Oliver Mtukudzi, Jah Prayzah".</summary>
public sealed record InterestDto(string Title, string Text);

/// <param name="Period">Free text, e.g. "2019 - now".</param>
public sealed record TimelineEntryDto(string Title, string? Place, string? Period, string? Description);

/// <param name="Kind">One of <see cref="SocialLinkKinds.All"/>.</param>
public sealed record SocialLinkDto(string Kind, string Url);

public static class SocialLinkKinds
{
    public static readonly IReadOnlyList<string> All = ["facebook", "x", "instagram", "tiktok", "youtube", "linkedin", "whatsapp", "website"];
}

public static class ProfileLimits
{
    public const int MaxSkills = 20;
    public const int MaxInterests = 10;
    public const int MaxTimeline = 12;
    public const int MaxLinks = 8;
    public const int MaxImageBytes = 10 * 1024 * 1024;
}

/// <summary>A person as a card: in People, Followers, Following and a group's Members.</summary>
public sealed record PersonCardDto(UserDto User, string? Bio, string? City, int PostCount, int FollowerCount, bool IFollow, bool IsShamwari);

public sealed record GroupSummaryDto(
    Guid Id,
    string Name,
    string Description,
    string Icon,
    string Tone,
    bool IsPrivate,
    int MemberCount,
    int PostCount,
    bool IsMember,
    string? MyRole,
    string? Tagline = null,
    string? AvatarUrl = null,
    string? CoverUrl = null);

public sealed record GroupDetailDto(
    GroupSummaryDto Group,
    string Rules,
    UserDto Owner,
    DateTimeOffset CreatedAt,
    IReadOnlyList<PersonCardDto> RecentMembers,
    string? Email = null,
    string? Website = null);

public sealed record SaveGroupRequest(
    string Name,
    string? Description,
    string? Rules,
    string? Icon,
    string? Tone,
    bool IsPrivate,
    string? Tagline = null,
    string? Email = null,
    string? Website = null);

public sealed record AddGroupMemberRequest(string Email);

/// <summary>Which posts a feed shows: everything you may see, or you, people you follow and your groups.</summary>
public static class FeedScopes
{
    public const string All = "all";
    public const string Following = "following";
}
