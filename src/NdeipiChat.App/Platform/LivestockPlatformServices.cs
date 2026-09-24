using NdeipiChat.Client;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Contracts;

namespace NdeipiChat.App.Platform;

public sealed class MauiLocationProvider : ILocationProvider
{
    public async Task<GpsTelemetry?> GetLocationAsync(CancellationToken ct)
    {
        var permission = await MainThread.InvokeOnMainThreadAsync(Permissions.RequestAsync<Permissions.LocationWhenInUse>);
        if (permission != PermissionStatus.Granted)
            return null;

        try
        {
            var location = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(15)), ct)
                ?? await Geolocation.Default.GetLastKnownLocationAsync();
            return location is null ? null : new GpsTelemetry(location.Latitude, location.Longitude, location.Accuracy ?? 0);
        }
        catch (FeatureNotEnabledException)
        {
            return null; // location switched off on the phone
        }
    }
}

public sealed class PreferencesSettingsStore : ISettingsStore
{
    public string? Get(string key) => Preferences.Default.Get<string?>(key, null);

    public void Set(string key, string value) => Preferences.Default.Set(key, value);
}

/// <summary>The operator's device signing key, in the Keychain (iOS) or Keystore-backed storage (Android).</summary>
public sealed class SecureOperatorKeyStore : IOperatorKeyStore
{
    static string Key(Guid userId) => $"ndeipi.operator-key.{userId:N}";

    public async Task<StoredOperatorKey?> LoadAsync(Guid userId)
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key(userId)) is { } json ? ContractJson.Read<StoredOperatorKey>(json) : null;
        }
        catch (Exception)
        {
            // Unreadable (e.g. restored onto another phone): a new key gets registered.
            SecureStorage.Default.Remove(Key(userId));
            return null;
        }
    }

    public Task SaveAsync(Guid userId, StoredOperatorKey key) => SecureStorage.Default.SetAsync(Key(userId), ContractJson.Write(key));

    public Task ClearAsync(Guid userId)
    {
        SecureStorage.Default.Remove(Key(userId));
        return Task.CompletedTask;
    }
}
