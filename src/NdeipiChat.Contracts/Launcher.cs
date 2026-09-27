namespace NdeipiChat.Contracts;

/// <summary>
/// The launcher: the host shell's grid of sub-apps, driven by a manifest the server builds for
/// each user (SRS SR-01). The built-in sub-apps ship with the shell; the manifest decides which ones
/// a user sees, in what order, and which are pinned. Apps a user's roles don't allow are left out.
/// </summary>
public static class LauncherContract
{
    public const string ManifestPath = "api/launcher";
    public const string PinsPath = "api/launcher/pins";
    public const int MaxPins = 8;
}

/// <summary>The built-in sub-apps. Their code ships with the shell, so they have no bundle to load.</summary>
public static class SubApps
{
    public const string Chats = "chats";
    public const string Feed = "feed";
    public const string Shamwaris = "shamwaris";
    public const string Wallet = "wallet";
    public const string Herd = "herd";
}

/// <param name="Version">Changes whenever the manifest's content does; clients cache by it.</param>
public sealed record LauncherManifestDto(string Version, IReadOnlyList<SubAppDto> Apps);

/// <summary>
/// One sub-app as this user sees it. <see cref="BundleUri"/> and <see cref="Sha256"/> are for
/// sub-apps loaded at runtime (SR-01-02, SR-02); both are null for built-in ones.
/// </summary>
/// <param name="Route">Where the shell opens it, e.g. "chats" or "herd".</param>
/// <param name="Scopes">What it's allowed to use, e.g. "banking" or "livestock".</param>
/// <param name="MinShellVersion">The oldest shell that can host it, as a semantic version.</param>
public sealed record SubAppDto(
    string Id,
    string Title,
    string Description,
    string Icon,
    string Route,
    string Version,
    string MinShellVersion,
    IReadOnlyList<string> Scopes,
    int Order,
    bool Pinned,
    string? BundleUri,
    string? Sha256);

/// <summary>The sub-apps to pin, in order. Ids the user can't use are ignored.</summary>
public sealed record SetPinsRequest(IReadOnlyList<string> AppIds);
