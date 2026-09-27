using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Launcher;

/// <summary>A published bundle: where it is, its hash, and the publisher's signature for that hash if there is one.</summary>
public sealed record SubAppBundle(string Uri, string Sha256, SubAppSignatureEntry? Signature);

/// <summary>
/// Finds a sub-app's published bundle -- the web app's lazy-loaded assembly, e.g.
/// _framework/NdeipiChat.SubApps.Inventory.abc123.wasm -- its SHA-256, and the publisher's signature
/// for it, for the manifest. The shell checks both before it runs the bundle (SRS NFR-02-01). This
/// server never signs anything: signatures come from the release, in subapp-signatures.json.
/// </summary>
public sealed partial class SubAppBundles(IWebHostEnvironment environment, IOptionsMonitor<LauncherOptions> options, ILogger<SubAppBundles> log)
{
    readonly ConcurrentDictionary<string, (DateTimeOffset Modified, string Sha256)> _hashes = new();
    (string Path, DateTime Modified, SubAppSignatureFile File)? _signatures;

    public SubAppBundle? Find(string assembly)
    {
        var folder = environment.WebRootFileProvider.GetDirectoryContents("_framework");
        var name = new Regex($"^{Regex.Escape(assembly)}(\\.[a-z0-9]+)?\\.wasm$", RegexOptions.IgnoreCase);
        var file = folder.Exists ? folder.FirstOrDefault(f => !f.IsDirectory && name.IsMatch(f.Name)) : null;
        if (file is null)
        {
            log.LogWarning("Sub-app bundle for {Assembly} isn't published; the app is left out of manifests", assembly);
            return null;
        }

        var sha256 = HashOf(file);
        // Only a signature for exactly this bundle: one left over from an earlier release doesn't count.
        var signature = Signatures().Signatures.FirstOrDefault(s =>
            s.Assembly.Equals(assembly, StringComparison.OrdinalIgnoreCase) && s.Sha256.Equals(sha256, StringComparison.OrdinalIgnoreCase));
        if (signature is null)
            log.LogWarning("Sub-app bundle {Assembly} ({Sha256}) isn't signed; shells that require signatures will refuse it", assembly, sha256);
        return new SubAppBundle($"_framework/{file.Name}", sha256, signature);
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

    /// <summary>subapp-signatures.json, re-read when it changes; none if it's missing or unreadable.</summary>
    SubAppSignatureFile Signatures()
    {
        var configured = options.CurrentValue.SignaturesPath;
        var path = Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured);
        if (!File.Exists(path))
            return new SubAppSignatureFile([]);

        var modified = File.GetLastWriteTimeUtc(path);
        if (_signatures is { } cached && cached.Path == path && cached.Modified == modified)
            return cached.File;
        try
        {
            var file = JsonSerializer.Deserialize<SubAppSignatureFile>(File.ReadAllText(path)) ?? new SubAppSignatureFile([]);
            _signatures = (path, modified, file);
            return file;
        }
        catch (JsonException ex)
        {
            log.LogError(ex, "Couldn't read sub-app signatures from {Path}", path);
            return new SubAppSignatureFile([]);
        }
    }
}
