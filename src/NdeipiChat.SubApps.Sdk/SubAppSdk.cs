using System.Reflection;
using System.Security.Cryptography;

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
public sealed record SubAppContext(
    string AppId,
    Guid UserId,
    string DisplayName,
    HttpClient Api,
    Func<Task> NavigateToLauncher);

public static class SubAppIntegrity
{
    /// <summary>
    /// Whether a bundle is exactly the one the manifest names (SRS NFR-02-01): its SHA-256, as
    /// lower-case hex, must match. A manifest without a hash never matches.
    /// </summary>
    public static bool Matches(byte[] bundle, string? expectedSha256)
    {
        if (expectedSha256 is not { Length: 64 } || !expectedSha256.All(char.IsAsciiHexDigit))
            return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(bundle), Convert.FromHexString(expectedSha256));
    }

    public static string Sha256Hex(byte[] bundle) => Convert.ToHexString(SHA256.HashData(bundle)).ToLowerInvariant();
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
