using System.Security.Cryptography;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Livestock;

/// <param name="PrivateKeyPkcs8">Base64 PKCS#8. Kept in the platform's secure storage, never sent anywhere.</param>
public sealed record StoredOperatorKey(Guid KeyId, string PrivateKeyPkcs8);

/// <summary>Per-user signing keys on this device -- SecureStorage (Keychain / Android Keystore) in the app.</summary>
public interface IOperatorKeyStore
{
    Task<StoredOperatorKey?> LoadAsync(Guid userId);
    Task SaveAsync(Guid userId, StoredOperatorKey key);
    Task ClearAsync(Guid userId);
}

public sealed class InMemoryOperatorKeyStore : IOperatorKeyStore
{
    readonly Dictionary<Guid, StoredOperatorKey> _keys = [];

    public Task<StoredOperatorKey?> LoadAsync(Guid userId) => Task.FromResult(_keys.GetValueOrDefault(userId));

    public Task SaveAsync(Guid userId, StoredOperatorKey key)
    {
        _keys[userId] = key;
        return Task.CompletedTask;
    }

    public Task ClearAsync(Guid userId)
    {
        _keys.Remove(userId);
        return Task.CompletedTask;
    }
}

/// <summary>
/// ECDSA on P-256 with SHA-256, keys as base64 SPKI (public) and PKCS#8 (private), signatures as
/// raw r||s -- what the API verifies. .NET's ECDsa in the app; the browser's Web Crypto in the web
/// app, where .NET has no ECDsa.
/// </summary>
public interface IP256Signer
{
    Task<(string PublicKeySpki, string PrivateKeyPkcs8)> CreateKeyAsync();
    Task<string> SignAsync(string privateKeyPkcs8, byte[] payload);
}

public sealed class DotNetP256Signer : IP256Signer
{
    public Task<(string PublicKeySpki, string PrivateKeyPkcs8)> CreateKeyAsync()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return Task.FromResult((Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()), Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey())));
    }

    public Task<string> SignAsync(string privateKeyPkcs8, byte[] payload)
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKeyPkcs8), out _);
        return Task.FromResult(Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256)));
    }
}

/// <summary>
/// Signs livestock registrations for the spec's X-Ndeipi-Signature. On first use it creates a
/// P-256 key on the device and registers the public half with the API; the private half stays here.
/// </summary>
public sealed class OperatorSigner(LivestockApi api, IOperatorKeyStore store, ChatSession session, ClientOptions options, IP256Signer? crypto = null)
{
    readonly SemaphoreSlim _gate = new(1, 1);
    readonly IP256Signer _crypto = crypto ?? new DotNetP256Signer();

    public async Task<(Guid KeyId, string Signature)> SignAsync(byte[] payload, CancellationToken ct = default)
    {
        var key = await EnsureKeyAsync(ct);
        return (key.KeyId, await _crypto.SignAsync(key.PrivateKeyPkcs8, payload));
    }

    /// <summary>Drops this device's key -- after the server stopped accepting it -- so the next signature registers a new one.</summary>
    public Task ForgetKeyAsync() => store.ClearAsync(session.MyUserId);

    async Task<StoredOperatorKey> EnsureKeyAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var userId = session.MyUserId;
            if (await store.LoadAsync(userId) is { } existing)
                return existing;

            var (publicKey, privateKey) = await _crypto.CreateKeyAsync();
            var registered = await api.RegisterOperatorKeyAsync(new OperatorKeyRequest(publicKey, options.DeviceName), ct);
            var key = new StoredOperatorKey(registered.KeyId, privateKey);
            await store.SaveAsync(userId, key);
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }
}
