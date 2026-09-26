using Microsoft.Extensions.Options;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Contracts;
using SkiaSharp;

namespace NdeipiChat.Api.Social;

public sealed class SocialOptions
{
    public const string Section = "Social";

    public string MediaPath { get; set; } = "App_Data/post-media";

    /// <summary>
    /// The site's public address, for URLs that outlive a request: NFT metadata and its image links.
    /// Empty means the address the request came in on.
    /// </summary>
    public string PublicBaseUrl { get; set; } = "";

    public NftOptions Nft { get; set; } = new();
}

/// <summary>The NFT contract posts are minted on. Minting is off until Chain and ContractAddress are set.</summary>
public sealed class NftOptions
{
    public string Chain { get; set; } = "";
    public string ContractAddress { get; set; } = "";
    public string Standard { get; set; } = TokenStandards.Erc721;
    public string Symbol { get; set; } = "NDPOST";
    public string CollectionName { get; set; } = "Ndeipi Posts";

    public bool Enabled => Chain.Length > 0 && ContractAddress.Length > 0;
}

/// <summary>
/// Post photos on local disk, re-encoded as JPEG in each of <see cref="PhotoSizes"/>. Re-encoding
/// turns the photo upright and drops its EXIF, GPS position included. Swap for blob storage when
/// running more than one API instance.
/// </summary>
public sealed class PostMediaStore(IOptions<SocialOptions> options, IWebHostEnvironment environment)
{
    string Root => Path.IsPathRooted(options.Value.MediaPath)
        ? options.Value.MediaPath
        : Path.Combine(environment.ContentRootPath, options.Value.MediaPath);

    public async Task SaveAsync(Guid mediaId, SKBitmap upright, CancellationToken ct)
    {
        Directory.CreateDirectory(Root);
        foreach (var (size, maxEdge) in PhotoSizes.MaxEdge)
        {
            var path = PathOf(mediaId, size);
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporary, CattleImages.ToJpeg(upright, maxEdge, size == PhotoSizes.Thumb ? 75 : 85), ct);
            File.Move(temporary, path, overwrite: true);
        }
    }

    public string? PathFor(Guid mediaId, string size) =>
        PhotoSizes.MaxEdge.ContainsKey(size) && File.Exists(PathOf(mediaId, size)) ? PathOf(mediaId, size) : null;

    public void Delete(Guid mediaId)
    {
        foreach (var size in PhotoSizes.MaxEdge.Keys)
            File.Delete(PathOf(mediaId, size));
    }

    string PathOf(Guid mediaId, string size) => Path.Combine(Root, $"{mediaId:N}-{size}.jpg");
}
