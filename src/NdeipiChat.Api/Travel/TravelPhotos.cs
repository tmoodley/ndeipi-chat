using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Livestock;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Travel;

public sealed class TravelOptions
{
    public const string Section = "Travel";

    /// <summary>Ndeipi's cut of each booking, taken by Bridge as its developer fee.</summary>
    public decimal FeePercent { get; set; } = 5m;

    /// <summary>
    /// FR-4.1: indicative rates from US dollars, for showing a price in other currencies at checkout.
    /// Bookings are charged in US dollars from the wallet; these are only shown, labelled approximate.
    /// </summary>
    public Dictionary<string, decimal> DisplayRates { get; set; } = new() { ["ZAR"] = 18.2m, ["ZMW"] = 26.5m, ["ZWG"] = 26.8m, ["BWP"] = 13.6m };

    /// <summary>FR-4.3: Believe Points per US dollar of discount. A discount comes out of Ndeipi's fee, never the vendor's share.</summary>
    public int PointsPerDollar { get; set; } = 100;

    /// <summary>Where listing photos are kept. Served only by expiring link.</summary>
    public string PhotoPath { get; set; } = "App_Data/travel-photos";

    public TimeSpan LinkLifetime { get; set; } = TimeSpan.FromHours(2);

    /// <summary>How often expired holds are put back on sale (acceptance 3: within 60 seconds of expiry).</summary>
    public TimeSpan SweepInterval { get; set; } = TimeSpan.FromSeconds(20);
}

/// <summary>
/// Photos of properties and tours: re-encoded (which strips location metadata) to a full size and a
/// thumbnail, and served through links that expire, so &lt;img&gt; can load them without a sign-in token.
/// </summary>
public sealed class TravelPhotos(ChatDbContext db, IDataProtectionProvider protection, IOptions<TravelOptions> options, IWebHostEnvironment environment, TimeProvider clock)
{
    const long MaxBytes = 15 * 1024 * 1024;

    ITimeLimitedDataProtector Links => protection.CreateProtector("travel-photos").ToTimeLimitedDataProtector();

    string Root => Path.IsPathRooted(options.Value.PhotoPath) ? options.Value.PhotoPath : Path.Combine(environment.ContentRootPath, options.Value.PhotoPath);

    public async Task<TravelPhoto> UploadAsync(Guid vendorId, IFormFile file, CancellationToken ct)
    {
        if (file.Length is 0 or > MaxBytes)
            throw new ChatRejectedException("Photos can be up to 15 MB.");
        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, ct);
        using var bitmap = CattleImages.DecodeUpright(buffer.ToArray()) ?? throw new ChatRejectedException("Upload a photo: JPEG, PNG or WebP.");
        var photo = new TravelPhoto { Id = Guid.NewGuid(), VendorId = vendorId, CreatedAt = clock.GetUtcNow() };
        Directory.CreateDirectory(Root);
        var full = CattleImages.ToJpeg(bitmap, 2048, 85);
        await File.WriteAllBytesAsync(PathOf(photo.Id, "full"), full, ct);
        await File.WriteAllBytesAsync(PathOf(photo.Id, "thumb"), CattleImages.ToJpeg(bitmap, 720, 78), ct);
        var scale = Math.Min(1d, 2048d / Math.Max(bitmap.Width, bitmap.Height));
        (photo.Width, photo.Height) = ((int)Math.Round(bitmap.Width * scale), (int)Math.Round(bitmap.Height * scale));
        db.TravelPhotos.Add(photo);
        await db.SaveChangesAsync(ct);
        return photo;
    }

    public TravelPhotoDto ToDto(Guid id) => new(id, Link(id, "full"), Link(id, "thumb"));

    public IReadOnlyList<TravelPhotoDto> ToDtos(string photoIds) => [.. Ids(photoIds).Select(ToDto)];

    public string? Cover(string photoIds) => Ids(photoIds).FirstOrDefault() is { } id && id != Guid.Empty ? Link(id, "thumb") : null;

    public static IReadOnlyList<Guid> Ids(string photoIds) =>
        [.. photoIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => Guid.TryParse(s, out var g) ? g : Guid.Empty).Where(g => g != Guid.Empty)];

    string Link(Guid id, string variant) =>
        $"{TravelContract.BasePath}/photos/{Uri.EscapeDataString(Links.Protect($"{id:N}|{variant}", options.Value.LinkLifetime))}";

    /// <summary>The file a link names, or null if the link is wrong or has expired.</summary>
    public string? Open(string token)
    {
        string payload;
        try
        {
            payload = Links.Unprotect(token);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return null;
        }
        if (payload.Split('|') is not [var id, var variant and ("full" or "thumb")] || !Guid.TryParseExact(id, "N", out var photoId))
            return null;
        var path = PathOf(photoId, variant);
        return File.Exists(path) ? path : null;
    }

    string PathOf(Guid id, string variant) => Path.Combine(Root, $"{id:N}-{variant}.jpg");
}
