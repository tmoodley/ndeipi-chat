using NdeipiChat.Client.Livestock;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client;

/// <summary>
/// Publisher keys a shell trusts to sign sub-apps, by <see cref="SubAppSigning.KeyId"/>. They're
/// built into the shell, not fetched: trusting a key the server hands over would prove nothing.
/// </summary>
public sealed class SubAppTrust
{
    /// <summary>Key id → base64 SPKI public key.</summary>
    public IReadOnlyDictionary<string, string> TrustedKeys { get; init; } = new Dictionary<string, string>();

    /// <summary>Refuse bundles that aren't signed by a trusted key. Only switched off for local development.</summary>
    public bool RequireSignature { get; init; } = true;

    public static SubAppTrust Of(bool requireSignature, params string[] publicKeys) => new()
    {
        TrustedKeys = publicKeys.ToDictionary(SubAppSigning.KeyId),
        RequireSignature = requireSignature
    };
}

/// <summary>
/// Decides whether a downloaded sub-app bundle may run (SRS NFR-02-01): it must be exactly the bundle
/// the manifest names (SHA-256), and signed for that hash by a publisher key this shell trusts.
/// </summary>
public sealed class SubAppVerifier(SubAppTrust trust, IP256Signer crypto)
{
    /// <summary>Null if the bundle may run; otherwise why not, worded for the user.</summary>
    public async Task<string?> ProblemWithAsync(SubAppDto app, byte[] bundle)
    {
        if (!SubAppSigning.HashMatches(bundle, app.Sha256))
            return $"{app.Title} didn't match the version your apps list expects, so it wasn't opened. Reload to get the latest.";

        if (app.Signature is null || app.SigningKeyId is null)
            return trust.RequireSignature ? $"{app.Title} isn't signed by its publisher, so it wasn't opened." : null;

        if (!trust.TrustedKeys.TryGetValue(app.SigningKeyId, out var publicKey))
            return $"{app.Title} is signed by a publisher this version of Ndeipi doesn't trust, so it wasn't opened.";

        var payload = SubAppSigning.Payload(app.Assembly ?? "", app.Sha256!);
        return await crypto.VerifyAsync(publicKey, payload, app.Signature)
            ? null
            : $"{app.Title}'s signature doesn't match its publisher's, so it wasn't opened.";
    }
}
