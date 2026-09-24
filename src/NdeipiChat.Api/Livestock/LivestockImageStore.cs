using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Livestock;

public interface ILivestockImageStore
{
    /// <summary>Stores a photo under its content hash and returns the reference, e.g. "3f…9a.jpg".</summary>
    Task<string> SaveAsync(byte[] data, string extension, CancellationToken ct);

    /// <summary>The file for a reference, or null if the reference is malformed or unknown.</summary>
    string? PathFor(string reference);
}

/// <summary>
/// Photos on local disk, named by SHA-256, so a photo uploaded twice is stored once. Swap for blob
/// storage when running more than one API instance.
/// </summary>
public sealed partial class FileSystemLivestockImageStore(IOptions<LivestockOptions> options, IWebHostEnvironment environment) : ILivestockImageStore
{
    [GeneratedRegex("^[0-9a-f]{64}\\.(jpg|png)$")]
    private static partial Regex ReferencePattern();

    string Root => Path.IsPathRooted(options.Value.ImageStoragePath)
        ? options.Value.ImageStoragePath
        : Path.Combine(environment.ContentRootPath, options.Value.ImageStoragePath);

    public async Task<string> SaveAsync(byte[] data, string extension, CancellationToken ct)
    {
        var reference = $"{LivestockContract.Sha256Hex(data)}.{extension}";
        Directory.CreateDirectory(Root);
        var path = Path.Combine(Root, reference);
        if (!File.Exists(path))
        {
            // Write aside and move into place, so a half-written file is never served.
            var temporary = $"{path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllBytesAsync(temporary, data, ct);
            File.Move(temporary, path, overwrite: true);
        }
        return reference;
    }

    public string? PathFor(string reference)
    {
        if (!ReferencePattern().IsMatch(reference))
            return null;
        var path = Path.Combine(Root, reference);
        return File.Exists(path) ? path : null;
    }
}
