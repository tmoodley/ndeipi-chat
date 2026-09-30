using System.Text.Json;
using Microsoft.JSInterop;
using NdeipiChat.SubApps.Sdk;

namespace NdeipiChat.SubApps.DevHost;

/// <summary>
/// The shell's device keys and values, the way the web shell does them: Web Crypto ECDSA P-256 and
/// localStorage, under names private to the app and the user.
/// </summary>
internal sealed class DevDevice(IJSRuntime js, string appId, Guid userId) : ISubAppDevice
{
    string Prefix => $"ndeipi.devhost.{appId}.{userId:N}.";

    public string DeviceName => "Dev host";

    public async Task<string> CreateKeyAsync(string name)
    {
        var pair = await js.InvokeAsync<string[]>("ndeipiDevHost.p256.createKey");
        await js.InvokeVoidAsync("ndeipiDevHost.storage.set", Prefix + "key." + name, JsonSerializer.Serialize(new StoredKey(pair[0], pair[1])));
        return pair[0];
    }

    public async Task<string?> GetPublicKeyAsync(string name) => (await LoadAsync(name))?.PublicKey;

    public async Task<string?> SignAsync(string name, byte[] payload) =>
        await LoadAsync(name) is { } key ? await js.InvokeAsync<string>("ndeipiDevHost.p256.sign", key.PrivateKey, payload) : null;

    public async Task<bool> VerifyAsync(string publicKeySpki, byte[] payload, string signature) =>
        await js.InvokeAsync<bool>("ndeipiDevHost.p256.verify", publicKeySpki, payload, signature);

    public async Task<string?> GetValueAsync(string name) =>
        await js.InvokeAsync<string?>("ndeipiDevHost.storage.get", Prefix + "value." + name);

    public async Task SetValueAsync(string name, string? value)
    {
        if (value is null)
            await js.InvokeVoidAsync("ndeipiDevHost.storage.remove", Prefix + "value." + name);
        else
            await js.InvokeVoidAsync("ndeipiDevHost.storage.set", Prefix + "value." + name, value);
    }

    async Task<StoredKey?> LoadAsync(string name)
    {
        var json = await js.InvokeAsync<string?>("ndeipiDevHost.storage.get", Prefix + "key." + name);
        try
        {
            return json is null ? null : JsonSerializer.Deserialize<StoredKey>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    sealed record StoredKey(string PublicKey, string PrivateKey);
}
