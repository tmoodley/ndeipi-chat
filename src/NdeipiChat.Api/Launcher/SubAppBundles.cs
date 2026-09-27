using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace NdeipiChat.Api.Launcher;

/// <summary>
/// Finds a sub-app's published bundle -- the web app's lazy-loaded assembly, e.g.
/// _framework/NdeipiChat.SubApps.Inventory.abc123.wasm -- and its SHA-256, for the manifest. The
/// shell checks the bundle against that hash before it runs it (SRS NFR-02-01).
/// </summary>
public sealed partial class SubAppBundles(IWebHostEnvironment environment, ILogger<SubAppBundles> log)
{
    readonly ConcurrentDictionary<string, (DateTimeOffset Modified, string Sha256)> _hashes = new();

    /// <summary>The bundle's URI (relative to the site) and SHA-256, or null if it isn't published.</summary>
    public (string Uri, string Sha256)? Find(string assembly)
    {
        var folder = environment.WebRootFileProvider.GetDirectoryContents("_framework");
        var name = new Regex($"^{Regex.Escape(assembly)}(\\.[a-z0-9]+)?\\.wasm$", RegexOptions.IgnoreCase);
        var file = folder.Exists ? folder.FirstOrDefault(f => !f.IsDirectory && name.IsMatch(f.Name)) : null;
        if (file is null)
        {
            log.LogWarning("Sub-app bundle for {Assembly} isn't published; the app is left out of manifests", assembly);
            return null;
        }
        return ($"_framework/{file.Name}", HashOf(file));
    }

    string HashOf(IFileInfo file)
    {
        var key = file.PhysicalPath ?? file.Name;
        if (_hashes.TryGetValue(key, out var known) && known.Modified == file.LastModified)
            return known.Sha256;

        using var stream = file.CreateReadStream();
        var sha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        _hashes[key] = (file.LastModified, sha256);
        return sha256;
    }
}
