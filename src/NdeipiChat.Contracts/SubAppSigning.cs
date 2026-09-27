using System.Security.Cryptography;
using System.Text;

namespace NdeipiChat.Contracts;

/// <summary>
/// How sub-app bundles are signed (SRS NFR-02-01). A publisher key, kept off the server, signs each
/// published bundle at release time (tools/NdeipiChat.SubAppSigner); the manifest carries the
/// signature; the shell checks it against publisher keys built into the shell before it runs the
/// bundle. Keys and signatures are ECDSA P-256 / SHA-256: base64 SPKI keys, raw r||s signatures.
/// </summary>
/// <summary>One signed bundle, as the signing tool writes it and the API reads it.</summary>
public sealed record SubAppSignatureEntry(string Assembly, string Sha256, string KeyId, string Signature);

/// <summary>subapp-signatures.json, published next to the API.</summary>
public sealed record SubAppSignatureFile(IReadOnlyList<SubAppSignatureEntry> Signatures);

public static class SubAppSigning
{
    public const string SignaturesFileName = "subapp-signatures.json";

    /// <summary>
    /// What's signed: the assembly and the exact bundle hash. A signature can't be moved to a
    /// different bundle, or a changed one.
    /// </summary>
    public static byte[] Payload(string assembly, string sha256) =>
        Encoding.UTF8.GetBytes($"ndeipi-subapp-v1\n{assembly}\n{sha256.ToLowerInvariant()}");

    /// <summary>A short, stable id for a public key: the first 16 hex digits of its SPKI's SHA-256.</summary>
    public static string KeyId(string publicKeySpkiBase64) =>
        Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(publicKeySpkiBase64)))[..16].ToLowerInvariant();

    public static string Sha256Hex(byte[] bundle) => Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant();

    /// <summary>
    /// Whether a bundle is exactly the one named: its SHA-256, as hex, must match. A missing or
    /// malformed hash never matches. Compared in constant time.
    /// </summary>
    public static bool HashMatches(byte[] bundle, string? expectedSha256)
    {
        if (expectedSha256 is not { Length: 64 } || !expectedSha256.All(char.IsAsciiHexDigit))
            return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(bundle), Convert.FromHexString(expectedSha256));
    }
}
