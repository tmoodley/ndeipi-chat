using System.Reflection;

namespace NdeipiChat.SubApps.Sdk;

/// <summary>
/// Marks a sub-app's root component: the one the shell renders when the launcher opens it
/// (SRS SR-02-03). A sub-app assembly has exactly one per app id.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SubAppRootAttribute(string appId) : Attribute
{
    public string AppId { get; } = appId;
}

/// <summary>
/// What the shell hands a sub-app, as a cascading parameter: who's signed in, and an HttpClient
/// for the Ndeipi API that already carries their sign-in (SRS SR-03-02). A sub-app never asks
/// anyone to sign in, and never sees a token.
/// </summary>
/// <param name="Api">Relative paths go to the Ndeipi API, e.g. "api/inventory"; the shell adds and refreshes the token.</param>
/// <param name="NavigateToLauncher">Takes the user back to the launcher.</param>
/// <param name="Realtime">Live updates over the shell's own connection; null in a shell that has none.</param>
/// <param name="Device">Keys and storage on this device, kept apart per sub-app and user; null in a shell that has none.</param>
/// <param name="Query">The query string the app was opened with, without "?", e.g. "chat={id}" from a chat's "+" panel; empty if none.</param>
public sealed record SubAppContext(
    string AppId,
    Guid UserId,
    string DisplayName,
    HttpClient Api,
    Func<Task> NavigateToLauncher,
    ISubAppRealtime? Realtime = null,
    ISubAppDevice? Device = null,
    string Query = "");

/// <summary>
/// Topics on the shell's realtime connection (e.g. "events:{id}"). The server decides who may
/// follow what; the shell re-subscribes after a reconnect.
/// </summary>
public interface ISubAppRealtime
{
    /// <summary>Calls <paramref name="onMessage"/> with each message's payload until the result is disposed.</summary>
    Task<IAsyncDisposable> SubscribeAsync(string topic, Func<System.Text.Json.JsonElement, Task> onMessage);
}

/// <summary>
/// ECDSA P-256 keys that never leave this device, and small stored values. Names are private to
/// the sub-app and the signed-in user. Keys are base64 SPKI; signatures base64 raw r||s.
/// </summary>
public interface ISubAppDevice
{
    string DeviceName { get; }

    /// <summary>Makes (or replaces) the key called <paramref name="name"/>; returns its public half.</summary>
    Task<string> CreateKeyAsync(string name);

    Task<string?> GetPublicKeyAsync(string name);

    /// <summary>The key's signature of <paramref name="payload"/>, or null if there's no such key here.</summary>
    Task<string?> SignAsync(string name, byte[] payload);

    Task<bool> VerifyAsync(string publicKeySpki, byte[] payload, string signature);

    Task<string?> GetValueAsync(string name);

    /// <summary>Stores a value; null removes it.</summary>
    Task SetValueAsync(string name, string? value);
}

public static class SubAppDiscovery
{
    /// <summary>The root component for <paramref name="appId"/> in a loaded sub-app assembly, or null.</summary>
    public static Type? FindRoot(IEnumerable<Assembly> assemblies, string appId) =>
        assemblies
            .SelectMany(a => a.GetExportedTypes())
            .FirstOrDefault(t => t.GetCustomAttribute<SubAppRootAttribute>()?.AppId.Equals(appId, StringComparison.OrdinalIgnoreCase) == true
                && typeof(Microsoft.AspNetCore.Components.IComponent).IsAssignableFrom(t));
}
