using System.Text.Json;
using System.Text.Json.Serialization;
using NdeipiChat.Contracts;
using NdeipiChat.SubApps.Farm.Models;
using NdeipiChat.SubApps.Sdk;

namespace NdeipiChat.SubApps.Farm.Services;

/// <summary>
/// Persists the farm in the shell's device storage, which keeps it apart per user. In a shell with
/// no device storage the farm lives only in memory for the session.
/// </summary>
public sealed class FarmStorage(ISubAppDevice? device)
{
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };

    public async Task SaveAsync(FarmState state)
    {
        if (device is not null) await device.SetValueAsync(FarmContract.SaveKey, JsonSerializer.Serialize(state, Options));
    }

    public async Task<FarmState?> LoadAsync()
    {
        if (device is null) return null;
        try
        {
            var json = await device.GetValueAsync(FarmContract.SaveKey);
            return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<FarmState>(json, Options);
        }
        catch
        {
            return null; // corrupt or incompatible save — start fresh
        }
    }

    public async Task ClearAsync()
    {
        if (device is not null) await device.SetValueAsync(FarmContract.SaveKey, null);
    }
}
