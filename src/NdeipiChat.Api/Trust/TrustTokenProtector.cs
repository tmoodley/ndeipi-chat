using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace NdeipiChat.Api.Trust;

/// <summary>
/// Encrypts linked accounts' OAuth tokens at rest (AES-GCM with Trust:TokenKey). With no key, nothing
/// is kept: <see cref="Protect"/> returns null.
/// </summary>
public sealed class TrustTokenProtector
{
    const int NonceSize = 12, TagSize = 16;
    readonly byte[]? _key;

    public TrustTokenProtector(IOptions<TrustOptions> options)
    {
        var configured = options.Value.TokenKey;
        if (configured.Length == 0)
            return;
        var key = Convert.FromBase64String(configured);
        _key = key.Length == 32 ? key : throw new InvalidOperationException("Trust:TokenKey must be 32 bytes, base64.");
    }

    public bool CanStore => _key is not null;

    public string? Protect(string? token)
    {
        if (_key is null || string.IsNullOrEmpty(token))
            return null;
        var plain = Encoding.UTF8.GetBytes(token);
        var output = new byte[NonceSize + TagSize + plain.Length];
        var nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize), output.AsSpan(NonceSize, TagSize));
        return Convert.ToBase64String(output);
    }

    /// <summary>The token, or null if there's none, no key, or it doesn't decrypt (the key changed).</summary>
    public string? Unprotect(string? protectedToken)
    {
        if (_key is null || string.IsNullOrEmpty(protectedToken))
            return null;
        try
        {
            var input = Convert.FromBase64String(protectedToken);
            if (input.Length < NonceSize + TagSize)
                return null;
            var plain = new byte[input.Length - NonceSize - TagSize];
            using var aes = new AesGcm(_key, TagSize);
            aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize + TagSize), input.AsSpan(NonceSize, TagSize), plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return null;
        }
    }
}
