using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubAppSigner;

/// <summary>
/// Signs published sub-app bundles with the publisher's private key, at release time and off the
/// server. The server only passes the signatures along, so taking it over doesn't let anyone sign.
/// </summary>
public static partial class SubAppBundleSigner
{
    /// <summary>A lazy-loaded sub-app bundle: NdeipiChat.SubApps.{Name}, with the publish fingerprint if any.</summary>
    [GeneratedRegex(@"^(?<assembly>NdeipiChat\.SubApps\.[A-Za-z0-9_.]+?)(?:\.[a-z0-9]{10})?\.wasm$")]
    private static partial Regex BundleName();

    /// <summary>The SDK is part of the shell, not a sub-app.</summary>
    const string SdkAssembly = "NdeipiChat.SubApps.Sdk";

    /// <summary>A new publisher key: the private half as PKCS#8 PEM, the public half as base64 SPKI.</summary>
    public static (string PrivateKeyPem, string PublicKeySpki) CreateKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportPkcs8PrivateKeyPem(), Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));
    }

    public static string PublicKeyOf(string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        return Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());
    }

    /// <summary>Signs every sub-app bundle under {site}/_framework.</summary>
    public static SubAppSignatureFile SignSite(string siteRoot, string privateKeyPem)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(privateKeyPem);
        var keyId = SubAppSigning.KeyId(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()));

        var signatures = new List<SubAppSignatureEntry>();
        foreach (var path in Directory.EnumerateFiles(Path.Combine(siteRoot, "_framework"), "NdeipiChat.SubApps.*.wasm"))
        {
            var match = BundleName().Match(Path.GetFileName(path));
            if (!match.Success || match.Groups["assembly"].Value == SdkAssembly)
                continue;
            var assembly = match.Groups["assembly"].Value;
            var sha256 = SubAppSigning.Sha256Hex(File.ReadAllBytes(path));
            var signature = ecdsa.SignData(SubAppSigning.Payload(assembly, sha256), HashAlgorithmName.SHA256);
            signatures.Add(new SubAppSignatureEntry(assembly, sha256, keyId, Convert.ToBase64String(signature)));
        }
        return new SubAppSignatureFile(signatures.OrderBy(s => s.Assembly, StringComparer.Ordinal).ToList());
    }

    public static string ToJson(SubAppSignatureFile file) =>
        JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true });
}
