using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Creators;

public sealed class CreatorsOptions
{
    public const string Section = "Creators";

    /// <summary>The platform's cut of every payment to a creator (FR-CR-11), taken by Bridge as its developer fee.</summary>
    public decimal FeePercent { get; set; } = 5m;

    /// <summary>Where creators' photos, audio and video are kept. Never served directly, only by expiring link.</summary>
    public string MediaPath { get; set; } = "App_Data/creator-media";

    /// <summary>How long a media link works (NFR-CR-06).</summary>
    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>How often renewals, grace periods and withdrawals are checked.</summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Creators' media (FR-CR-08): photos re-encoded to a full size and a thumbnail; audio and video kept
/// as sent. Every file is served through a link that names it and expires (FR-CR-09, NFR-CR-06), handed
/// out only to someone who may see it.
/// </summary>
public sealed class CreatorMediaService(ChatDbContext db, IOptions<CreatorsOptions> options, IDataProtectionProvider protection,
    IWebHostEnvironment environment, TimeProvider clock)
{
    public const string Full = "full", Thumb = "thumb", File = "file";
    public const string Audio = "audio";

    ITimeLimitedDataProtector Links => protection.CreateProtector("creator-media").ToTimeLimitedDataProtector();

    string Root => Path.IsPathRooted(options.Value.MediaPath) ? options.Value.MediaPath : Path.Combine(environment.ContentRootPath, options.Value.MediaPath);

    public async Task<CreatorMedia> UploadAsync(Guid creatorId, IFormFile file, CancellationToken ct)
    {
        if (file.Length == 0)
            throw new ChatRejectedException("That file is empty.");
        if (file.Length > CreatorsContract.MaxAudioVideoBytes && file.Length > CreatorsContract.MaxImageBytes)
            throw new ChatRejectedException($"Files can be up to {CreatorsContract.MaxAudioVideoBytes / 1024 / 1024} MB.");

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        var media = new CreatorMedia
        {
            Id = Guid.NewGuid(),
            CreatorId = creatorId,
            Type = MediaTypes.Image,
            ContentType = "image/jpeg",
            FileName = Path.GetFileName(file.FileName) is { Length: > 0 and <= 200 } name ? name : null,
            Size = bytes.Length,
            CreatedAt = clock.GetUtcNow()
        };
        Directory.CreateDirectory(Root);

        if (Sniff(bytes) is { } kind)
        {
            if (bytes.Length > CreatorsContract.MaxAudioVideoBytes)
                throw new ChatRejectedException($"Audio and video can be up to {CreatorsContract.MaxAudioVideoBytes / 1024 / 1024} MB.");
            (media.Type, media.ContentType) = (kind.Type, kind.ContentType);
            await System.IO.File.WriteAllBytesAsync(PathOf(media.Id, File + kind.Extension), bytes, ct);
        }
        else
        {
            if (bytes.Length > CreatorsContract.MaxImageBytes)
                throw new ChatRejectedException($"Photos can be up to {CreatorsContract.MaxImageBytes / 1024 / 1024} MB.");
            using var bitmap = CattleImages.DecodeUpright(bytes)
                ?? throw new ChatRejectedException("Upload a photo (JPEG, PNG or WebP), audio (MP3, M4A, OGG or WAV) or video (MP4, MOV or WebM).");
            var full = CattleImages.ToJpeg(bitmap, 2048, 85);
            await System.IO.File.WriteAllBytesAsync(PathOf(media.Id, Full + ".jpg"), full, ct);
            await System.IO.File.WriteAllBytesAsync(PathOf(media.Id, Thumb + ".jpg"), CattleImages.ToJpeg(bitmap, 640, 75), ct);
            var scale = Math.Min(1d, 2048d / Math.Max(bitmap.Width, bitmap.Height));
            (media.Width, media.Height, media.Size) = ((int)Math.Round(bitmap.Width * scale), (int)Math.Round(bitmap.Height * scale), full.Length);
        }

        db.CreatorMedia.Add(media);
        await db.SaveChangesAsync(ct);
        return media;
    }

    /// <summary>Audio or video, from its first bytes; null for anything else (a photo, or not media).</summary>
    static (string Type, string ContentType, string Extension)? Sniff(byte[] b)
    {
        if (b is [0x49, 0x44, 0x33, ..] or [0xFF, 0xFB or 0xF3 or 0xF2, ..])
            return (Audio, "audio/mpeg", ".mp3");
        if (b is [0x4F, 0x67, 0x67, 0x53, ..])
            return (Audio, "audio/ogg", ".ogg");
        if (b is [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x41, 0x56, 0x45, ..])
            return (Audio, "audio/wav", ".wav");
        if (b is [0xFF, 0xF1 or 0xF9, ..])
            return (Audio, "audio/aac", ".aac");
        if (b is [0x1A, 0x45, 0xDF, 0xA3, ..])
            return (MediaTypes.Video, "video/webm", ".webm");
        if (b.Length < 12 || b[4] != 'f' || b[5] != 't' || b[6] != 'y' || b[7] != 'p')
            return null;
        var brand = System.Text.Encoding.ASCII.GetString(b, 8, 4);
        if (brand is "heic" or "heix" or "mif1" or "msf1" or "avif" or "heim" or "heis")
            return null;
        if (brand.StartsWith("M4A", StringComparison.Ordinal) || brand.StartsWith("M4B", StringComparison.Ordinal))
            return (Audio, "audio/mp4", ".m4a");
        return brand.StartsWith("qt", StringComparison.Ordinal) ? (MediaTypes.Video, "video/quicktime", ".mov")
            : brand.StartsWith("3g", StringComparison.Ordinal) ? (MediaTypes.Video, "video/3gpp", ".3gp")
            : (MediaTypes.Video, "video/mp4", ".mp4");
    }

    string PathOf(Guid id, string name) => Path.Combine(Root, $"{id:N}-{name}");

    /// <summary>A link to one file of a media item that works for <see cref="CreatorsOptions.LinkLifetime"/>.</summary>
    public string Link(Guid mediaId, string variant) =>
        $"{CreatorsContract.BasePath}/media/{Uri.EscapeDataString(Links.Protect($"{mediaId:N}|{variant}", options.Value.LinkLifetime))}";

    public CreatorMediaDto ToDto(CreatorMedia m, bool withLinks) => m.Type == MediaTypes.Image
        ? new CreatorMediaDto(m.Id, m.Type, withLinks ? Link(m.Id, Full) : null, withLinks ? Link(m.Id, Thumb) : null, m.Width, m.Height, m.Size, m.FileName)
        : new CreatorMediaDto(m.Id, m.Type, withLinks ? Link(m.Id, File) : null, null, m.Width, m.Height, m.Size, m.FileName);

    /// <summary>The file behind a link, or null if it's wrong, run out, or gone.</summary>
    public async Task<(string Path, string ContentType)?> OpenAsync(string token, CancellationToken ct)
    {
        string payload;
        try
        {
            payload = Links.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }
        var parts = payload.Split('|');
        if (parts.Length != 2 || !Guid.TryParseExact(parts[0], "N", out var id))
            return null;
        var media = await db.CreatorMedia.AsNoTracking().FirstOrDefaultAsync(m => m.Id == id, ct);
        if (media is null)
            return null;
        if (parts[1] is Full or Thumb)
            return System.IO.File.Exists(PathOf(id, parts[1] + ".jpg")) ? (PathOf(id, parts[1] + ".jpg"), "image/jpeg") : null;
        var path = Directory.Exists(Root) ? Directory.EnumerateFiles(Root, $"{id:N}-{File}.*").FirstOrDefault() : null;
        return path is null ? null : (path, media.ContentType);
    }
}
