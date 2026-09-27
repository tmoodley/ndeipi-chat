using NdeipiChat.Contracts;

namespace NdeipiChat.Web.Platform;

/// <summary>Which of this site's pages belong to which sub-app, for the shell's access check.</summary>
public static class WebSubApps
{
    /// <summary>First path segment → sub-app id. Pages not listed (the launcher, Me) belong to the shell.</summary>
    static readonly Dictionary<string, string> Owners = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chats"] = SubApps.Chats,
        ["chat"] = SubApps.Chats,
        ["feed"] = SubApps.Feed,
        ["posts"] = SubApps.Feed,
        ["shamwaris"] = SubApps.Shamwaris,
        ["wallet"] = SubApps.Wallet,
        ["herd"] = SubApps.Herd
    };

    /// <summary>The sub-app a page belongs to, or null for the shell's own pages.</summary>
    public static string? OwnerOf(string relativePath) =>
        Owners.GetValueOrDefault(relativePath.Split('?', '#')[0].Split('/')[0]);

    /// <summary>Whether this site has pages for a manifest route. Sub-apps loaded at runtime come in step 2.</summary>
    public static bool HasPages(string route) => Owners.ContainsKey(route.Split('/')[0]);
}
