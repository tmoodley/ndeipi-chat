using System.Text.Json;
using Microsoft.JSInterop;
using NdeipiChat.Client;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Contracts;

namespace NdeipiChat.Web.Platform;

/// <summary>
/// Captures waiting to upload, in IndexedDB with their photos (ndeipi.js). Like the phone's SQLite
/// queue, they survive reloads and closing the tab, and go up when the connection is back.
/// </summary>
public sealed class IndexedDbCaptureQueue(IJSRuntime js, TimeProvider clock) : ILivestockCaptureQueue
{
    sealed record Row(Guid Id, DateTimeOffset CreatedAt, string Label, string MetadataJson, string Status, int Attempts, string? LastError);

    public async Task<PendingCapture> EnqueueAsync(byte[] face, byte[] flank, string metadataJson, string label, CancellationToken ct = default)
    {
        var capture = new PendingCapture(Guid.NewGuid(), clock.GetUtcNow(), label, metadataJson, "", "", CaptureStatuses.Pending, 0, null);
        await Run(() => js.InvokeVoidAsync("ndeipi.captures.put", ct, ToRow(capture), face, flank));
        return capture;
    }

    public async Task<IReadOnlyList<PendingCapture>> ListAsync(CancellationToken ct = default) =>
        (await Run(() => js.InvokeAsync<Row[]>("ndeipi.captures.list", ct))).Select(FromRow).OrderBy(c => c.CreatedAt).ToList();

    public async Task<PendingCapture?> GetAsync(Guid id, CancellationToken ct = default) =>
        await Run(() => js.InvokeAsync<Row?>("ndeipi.captures.get", ct, id)) is { } row ? FromRow(row) : null;

    public Task UpdateAsync(PendingCapture capture, CancellationToken ct = default) =>
        Run(() => js.InvokeVoidAsync("ndeipi.captures.update", ct, ToRow(capture)));

    public Task RemoveAsync(Guid id, CancellationToken ct = default) =>
        Run(() => js.InvokeVoidAsync("ndeipi.captures.remove", ct, id));

    public async Task<(byte[] Face, byte[] Flank)> ReadPhotosAsync(PendingCapture capture, CancellationToken ct = default)
    {
        var face = await Run(() => js.InvokeAsync<byte[]?>("ndeipi.captures.photo", ct, capture.Id, "face"));
        var flank = await Run(() => js.InvokeAsync<byte[]?>("ndeipi.captures.photo", ct, capture.Id, "flank"));
        return face is not null && flank is not null
            ? (face, flank)
            : throw new InvalidOperationException("This capture's photos are missing from the browser.");
    }

    static Row ToRow(PendingCapture c) => new(c.Id, c.CreatedAt, c.Label, c.MetadataJson, c.Status, c.Attempts, c.LastError);

    static PendingCapture FromRow(Row r) => new(r.Id, r.CreatedAt, r.Label, r.MetadataJson, "", "", r.Status, r.Attempts, r.LastError);

    /// <summary>Storage failures (private browsing, quota) surface as the queue's usual InvalidOperationException.</summary>
    static async Task Run(Func<ValueTask> work)
    {
        try
        {
            await work();
        }
        catch (JSException ex)
        {
            throw new InvalidOperationException("The browser couldn't store the photos: " + ex.Message, ex);
        }
    }

    static async Task<T> Run<T>(Func<ValueTask<T>> work)
    {
        try
        {
            return await work();
        }
        catch (JSException ex)
        {
            throw new InvalidOperationException("The browser couldn't read the stored photos: " + ex.Message, ex);
        }
    }
}

/// <summary>Web Crypto's ECDSA, since .NET's ECDsa isn't available in the browser.</summary>
public sealed class WebCryptoP256Signer(IJSRuntime js) : IP256Signer
{
    public async Task<(string PublicKeySpki, string PrivateKeyPkcs8)> CreateKeyAsync()
    {
        var pair = await js.InvokeAsync<string[]>("ndeipi.p256.createKey");
        return (pair[0], pair[1]);
    }

    public async Task<string> SignAsync(string privateKeyPkcs8, byte[] payload) =>
        await js.InvokeAsync<string>("ndeipi.p256.sign", privateKeyPkcs8, payload);
}

/// <summary>
/// The operator's signing key, in this browser's localStorage. The phone keeps it in the Keychain or
/// Keystore; a browser has nothing like that, so anyone who can run script on this site could read it.
/// The server can revoke a key, and the app registers a fresh one when that happens.
/// </summary>
public sealed class BrowserOperatorKeyStore(BrowserStorage storage) : IOperatorKeyStore
{
    static string Key(Guid userId) => $"ndeipi.operator-key.{userId:N}";

    public async Task<StoredOperatorKey?> LoadAsync(Guid userId)
    {
        var json = await storage.GetAsync("localStorage", Key(userId));
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<StoredOperatorKey>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task SaveAsync(Guid userId, StoredOperatorKey key) =>
        await storage.SetAsync("localStorage", Key(userId), JsonSerializer.Serialize(key));

    public async Task ClearAsync(Guid userId) => await storage.RemoveAsync("localStorage", Key(userId));
}

/// <summary>The browser's geolocation. It asks the farmer the first time.</summary>
public sealed class BrowserLocationProvider(IJSRuntime js) : ILocationProvider
{
    public async Task<GpsTelemetry?> GetLocationAsync(CancellationToken ct) =>
        await js.InvokeAsync<GpsTelemetry?>("ndeipi.location", ct);
}

/// <summary>
/// Remembered values such as the ranch code, in localStorage. The interface is synchronous, which
/// JS interop can be in WebAssembly.
/// </summary>
public sealed class BrowserSettingsStore(IJSRuntime js) : ISettingsStore
{
    IJSInProcessRuntime Js => (IJSInProcessRuntime)js;

    public string? Get(string key) => Js.Invoke<string?>("ndeipi.storage.get", "localStorage", "ndeipi.setting." + key);

    public void Set(string key, string value) => Js.InvokeVoid("ndeipi.storage.set", "localStorage", "ndeipi.setting." + key, value);
}

/// <summary>API photos (thumbnails, a cow's face) are small enough to put in the page as data URLs.</summary>
public static class ImageUrls
{
    public static string? Data(byte[]? image) => image is null
        ? null
        : $"data:{(image is [0x89, 0x50, ..] ? "image/png" : "image/jpeg")};base64,{Convert.ToBase64String(image)}";
}
