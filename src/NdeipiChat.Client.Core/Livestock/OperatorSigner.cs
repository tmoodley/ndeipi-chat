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
/// Signs livestock registrations for the spec's X-Ndeipi-Signature. On first use it creates a
/// P-256 key on the device and registers the public half with the API; the private half stays here.
/// </summary>
public sealed class OperatorSigner(LivestockApi api, IOperatorKeyStore store, ChatSession session, ClientOptions options)
{
    readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<(Guid KeyId, string Signature)> SignAsync(byte[] payload, CancellationToken ct = default)
    {
        var key = await EnsureKeyAsync(ct);
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key.PrivateKeyPkcs8), out _);
        return (key.KeyId, Convert.ToBase64String(ecdsa.SignData(payload, HashAlgorithmName.SHA256)));
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

            using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var registered = await api.RegisterOperatorKeyAsync(
                new OperatorKeyRequest(Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo()), options.DeviceName), ct);
            var key = new StoredOperatorKey(registered.KeyId, Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey()));
            await store.SaveAsync(userId, key);
            return key;
        }
        finally
        {
            _gate.Release();
        }
    }
}
