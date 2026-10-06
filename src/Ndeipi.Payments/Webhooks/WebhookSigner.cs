using System.Text;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.X509;

namespace Ndeipi.Payments.Webhooks;

/// <summary>
/// Webhook signatures (FR-WH-02, FR-WH-03; docs/payments-api/README.md, decision 1). Each endpoint
/// has its own Ed25519 key pair. A delivery carries
/// <c>Ndeipi-Signature: t=&lt;unix seconds&gt;,v1=&lt;base64 signature&gt;</c>, where the signature is
/// Ed25519 over the bytes <c>"&lt;t&gt;." + raw body</c> and <c>t</c> is the time of that delivery
/// attempt, so retries over the two-day window still pass a 10-minute tolerance.
///
/// <see cref="Verify"/> is the reference the SDK verifiers must agree with (DC-09).
/// </summary>
public static class WebhookSigner
{
    public const string Header = "Ndeipi-Signature";

    public sealed record KeyPair(byte[] PrivateKey, string PublicKeyPem);

    public static KeyPair GenerateKeyPair()
    {
        var privateKey = new Ed25519PrivateKeyParameters(new SecureRandom());
        var spki = SubjectPublicKeyInfoFactory.CreateSubjectPublicKeyInfo(privateKey.GeneratePublicKey()).GetEncoded();
        return new KeyPair(privateKey.GetEncoded(), new string(System.Security.Cryptography.PemEncoding.Write("PUBLIC KEY", spki)) + "\n");
    }

    /// <summary>The header value for one delivery attempt. Several keys (during rotation) give several v1 entries.</summary>
    public static string Sign(ReadOnlySpan<byte> body, DateTimeOffset attemptAt, params byte[][] privateKeys)
    {
        var t = attemptAt.ToUnixTimeSeconds();
        var message = Message(t, body);
        var header = new StringBuilder($"t={t}");
        foreach (var key in privateKeys)
        {
            var signer = new Ed25519Signer();
            signer.Init(true, new Ed25519PrivateKeyParameters(key));
            signer.BlockUpdate(message, 0, message.Length);
            header.Append(",v1=").Append(Convert.ToBase64String(signer.GenerateSignature()));
        }
        return header.ToString();
    }

    /// <summary>
    /// True when the header is well formed, <c>t</c> is within <paramref name="tolerance"/> of
    /// <paramref name="now"/>, and any one <c>v1</c> signature verifies over the raw body.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> body, string? header, string publicKeyPem, DateTimeOffset now, TimeSpan tolerance)
    {
        if (string.IsNullOrEmpty(header))
            return false;

        long? t = null;
        var signatures = new List<byte[]>();
        foreach (var part in header.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
                return false;
            if (pair[0] == "t" && long.TryParse(pair[1], out var seconds))
                t = seconds;
            else if (pair[0] == "v1")
            {
                try { signatures.Add(Convert.FromBase64String(pair[1])); }
                catch (FormatException) { return false; }
            }
        }
        if (t is null || signatures.Count == 0)
            return false;
        if ((now - DateTimeOffset.FromUnixTimeSeconds(t.Value)).Duration() > tolerance)
            return false;

        Ed25519PublicKeyParameters publicKey;
        try
        {
            publicKey = (Ed25519PublicKeyParameters)PublicKeyFactory.CreateKey(PemBytes(publicKeyPem));
        }
        catch (Exception e) when (e is ArgumentException or InvalidCastException or FormatException)
        {
            return false;
        }

        var message = Message(t.Value, body);
        foreach (var signature in signatures)
        {
            var verifier = new Ed25519Signer();
            verifier.Init(false, publicKey);
            verifier.BlockUpdate(message, 0, message.Length);
            if (verifier.VerifySignature(signature))
                return true;
        }
        return false;
    }

    static byte[] Message(long t, ReadOnlySpan<byte> body)
    {
        var prefix = Encoding.ASCII.GetBytes($"{t}.");
        var message = new byte[prefix.Length + body.Length];
        prefix.CopyTo(message, 0);
        body.CopyTo(message.AsSpan(prefix.Length));
        return message;
    }

    static byte[] PemBytes(string pem)
    {
        var fields = System.Security.Cryptography.PemEncoding.Find(pem);
        return Convert.FromBase64String(pem[fields.Base64Data]);
    }
}
