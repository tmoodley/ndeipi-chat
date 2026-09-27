using System.Text.Json;
using NdeipiChat.Client;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;
using NdeipiChat.SubApps.Sdk;

namespace NdeipiChat.Web.Platform;

/// <summary>A sub-app's topics, over the shell's one SignalR connection.</summary>
public sealed class ShellRealtime(ChatConnection connection) : ISubAppRealtime
{
    public async Task<IAsyncDisposable> SubscribeAsync(string topic, Func<JsonElement, Task> onMessage)
    {
        var subscription = new Subscription(connection, topic, onMessage);
        connection.TopicMessageReceived += subscription.Receive;
        try
        {
            await connection.SubscribeAsync(topic);
        }
        catch
        {
            connection.TopicMessageReceived -= subscription.Receive;
            throw;
        }
        return subscription;
    }

    sealed class Subscription(ChatConnection connection, string topic, Func<JsonElement, Task> onMessage) : IAsyncDisposable
    {
        bool _disposed;

        public void Receive(TopicMessageDto message)
        {
            if (!_disposed && message.Topic == topic)
                _ = onMessage(message.Payload);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            connection.TopicMessageReceived -= Receive;
            try
            {
                await connection.UnsubscribeAsync(topic);
            }
            catch (Exception)
            {
                // Disconnected: the server has already dropped the group.
            }
        }
    }
}

/// <summary>
/// A sub-app's keys and values in this browser's localStorage, under names private to the app and
/// the user, signing with Web Crypto. The private key is readable only by this origin's code.
/// </summary>
public sealed class ShellDevice(string appId, Guid userId, BrowserStorage storage, IP256Signer signer, ClientOptions options) : ISubAppDevice
{
    const string Area = "localStorage";

    string Prefix => $"ndeipi.subapp.{appId}.{userId:N}.";

    public string DeviceName => options.DeviceName;

    public async Task<string> CreateKeyAsync(string name)
    {
        var (publicKey, privateKey) = await signer.CreateKeyAsync();
        await storage.SetAsync(Area, Prefix + "key." + name, JsonSerializer.Serialize(new StoredKey(publicKey, privateKey)));
        return publicKey;
    }

    public async Task<string?> GetPublicKeyAsync(string name) => (await LoadAsync(name))?.PublicKey;

    public async Task<string?> SignAsync(string name, byte[] payload) =>
        await LoadAsync(name) is { } key ? await signer.SignAsync(key.PrivateKey, payload) : null;

    public Task<bool> VerifyAsync(string publicKeySpki, byte[] payload, string signature) =>
        signer.VerifyAsync(publicKeySpki, payload, signature);

    public async Task<string?> GetValueAsync(string name) => await storage.GetAsync(Area, Prefix + "value." + name);

    public async Task SetValueAsync(string name, string? value)
    {
        if (value is null)
            await storage.RemoveAsync(Area, Prefix + "value." + name);
        else
            await storage.SetAsync(Area, Prefix + "value." + name, value);
    }

    async Task<StoredKey?> LoadAsync(string name)
    {
        var json = await storage.GetAsync(Area, Prefix + "key." + name);
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
