using NdeipiChat.Contracts;

namespace NdeipiChat.Web.Platform;

/// <summary>Which of this site's pages belong to which sub-app, for the shell's access check.</summary>
public static class WebSubApps
{
    /// <summary>First path segment → sub-app id. Pages not listed (the launcher, Me) belong to the shell.</summary>
    static readonly Dictionary<string, string> Owners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chats"] = BuiltInApps.Chats,
        ["chat"] = BuiltInApps.Chats,
        ["feed"] = BuiltInApps.Feed,
        ["posts"] = BuiltInApps.Feed,
        ["shamwaris"] = BuiltInApps.Shamwaris,
        ["wallet"] = BuiltInApps.Wallet,
        ["herd"] = BuiltInApps.Herd
    };

    /// <summary>Where runtime-loaded sub-apps open: apps/{id} (Pages/SubAppHost.razor).</summary>
    public const string LoadedAppsPrefix = "apps";

    /// <summary>The sub-app a page belongs to, or null for the shell's own pages.</summary>
    public static string? OwnerOf(string relativePath)
    {
        var segments = relativePath.Split('?', '#')[0].Split('/');
        return segments[0].Equals(LoadedAppsPrefix, StringComparison.OrdinalIgnoreCase) && segments.Length > 1
            ? segments[1].ToLowerInvariant()
            : Owners.GetValueOrDefault(segments[0]);
    }

    /// <summary>Whether this site can open a manifest route: a built-in app's pages, or apps/{id} for a loaded one.</summary>
    public static bool HasPages(string route) =>
        Owners.ContainsKey(route.Split('/')[0]) || route.StartsWith(LoadedAppsPrefix + "/", StringComparison.OrdinalIgnoreCase);
}
