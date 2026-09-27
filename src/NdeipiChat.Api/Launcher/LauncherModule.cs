using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Launcher;

/// <summary>
/// Settings for one sub-app. For a built-in app, anything set here overrides the catalogue; an id
/// the catalogue doesn't know adds a new app (Title and Route are then required).
/// </summary>
public sealed class SubAppOptions
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Icon { get; set; }
    public string? Route { get; set; }
    public string? Version { get; set; }
    public string? MinShellVersion { get; set; }
    public int? Order { get; set; }
    public bool? DefaultPinned { get; set; }
    public bool? Enabled { get; set; }
    public string[]? Scopes { get; set; }

    /// <summary>Any one of these Clerk roles lets a user have the app. Empty means everyone.</summary>
    public string[]? RequiredRoles { get; set; }

    /// <summary>
    /// A sub-app the web shell loads at runtime: its assembly name, e.g. "NdeipiChat.SubApps.Inventory".
    /// The manifest then carries the published bundle's URI and SHA-256, worked out from the files.
    /// </summary>
    public string? Assembly { get; set; }

    public string? BundleUri { get; set; }
    public string? Sha256 { get; set; }
}

public sealed class LauncherOptions
{
    public const string Section = "Launcher";

    /// <summary>By sub-app id, e.g. "herd": { "RequiredRoles": [ "farmer" ] }.</summary>
    public Dictionary<string, SubAppOptions> Apps { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>A sub-app after the catalogue and configuration are merged.</summary>
public sealed record SubApp(
    string Id,
    string Title,
    string Description,
    string Icon,
    string Route,
    string Version,
    string MinShellVersion,
    int Order,
    bool DefaultPinned,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> RequiredRoles,
    string? BundleUri,
    string? Sha256,
    string? Assembly = null)
{
    public bool AllowedFor(User user)
    {
        if (RequiredRoles.Count == 0)
            return true;
        var roles = user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries);
        return RequiredRoles.Any(r => roles.Contains(r, StringComparer.OrdinalIgnoreCase));
    }
}

/// <summary>Builds each user's launcher manifest (SRS SR-01) and decides who may use which sub-app (SR-03-03).</summary>
public sealed class LauncherService(IOptionsMonitor<LauncherOptions> options, ChatDbContext db, SubAppBundles bundles)
{
    /// <summary>The sub-apps that ship with the shell.</summary>
    static readonly SubApp[] Catalogue =
    [
        new(BuiltInApps.Chats, "Chats", "Messages, and money and tokens sent in chats.", "💬", "chats", "1.0.0", "1.0.0", 10, true, ["chat"], [], null, null),
        new(BuiltInApps.Feed, "Feed", "Photo posts, which you can mint as NFTs.", "📷", "feed", "1.0.0", "1.0.0", 20, true, ["social", "nft"], [], null, null),
        new(BuiltInApps.Shamwaris, "Shamwaris", "Your friends, and adding people by email or phone.", "👥", "shamwaris", "1.0.0", "1.0.0", 30, true, ["contacts"], [], null, null),
        new(BuiltInApps.Wallet, "Wallet", "Identity verification, balances and receiving wallets.", "👛", "wallet", "1.0.0", "1.0.0", 40, false, ["banking"], [], null, null),
        new(BuiltInApps.Herd, "Herd", "Register cattle and track their health.", "🐄", "herd", "1.0.0", "1.0.0", 50, true, ["livestock"], [], null, null)
    ];

    public IReadOnlyList<SubApp> All()
    {
        var configured = options.CurrentValue.Apps;
        var apps = new List<SubApp>();
        foreach (var app in Catalogue)
        {
            var merged = configured.TryGetValue(app.Id, out var o) ? Merge(app, o) : app;
            if (o?.Enabled != false)
                apps.Add(merged);
        }
        foreach (var (id, o) in configured.Where(c => Catalogue.All(a => !a.Id.Equals(c.Key, StringComparison.OrdinalIgnoreCase))))
        {
            if (o.Enabled == false || string.IsNullOrWhiteSpace(o.Title) || string.IsNullOrWhiteSpace(o.Route))
                continue;
            apps.Add(Merge(new SubApp(id.ToLowerInvariant(), o.Title, "", "▦", o.Route, "1.0.0", "1.0.0", 100, false, [], [], null, null), o));
        }
        return apps
            .Select(WithBundle)
            .OfType<SubApp>()
            .OrderBy(a => a.Order)
            .ThenBy(a => a.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A runtime-loaded sub-app with its published bundle's URI and hash filled in; null (left out)
    /// if the bundle isn't published, since the shell couldn't open it.
    /// </summary>
    SubApp? WithBundle(SubApp app)
    {
        if (app.Assembly is null || app.BundleUri is not null)
            return app;
        return bundles.Find(app.Assembly) is { } bundle ? app with { BundleUri = bundle.Uri, Sha256 = bundle.Sha256 } : null;
    }

    public SubApp? Find(string id) => All().FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    public bool CanUse(User user, string appId) => Find(appId)?.AllowedFor(user) == true;

    public LauncherManifestDto ManifestFor(User user)
    {
        var apps = All().Where(a => a.AllowedFor(user)).ToList();
        var pins = PinsOf(user, apps);
        var dtos = apps.Select(a => new SubAppDto(
            a.Id, a.Title, a.Description, a.Icon, a.Route, a.Version, a.MinShellVersion, a.Scopes,
            a.Order, pins.Contains(a.Id), a.BundleUri, a.Sha256, a.Assembly)).ToList();

        // Pinned apps first, in the user's order; then the rest in catalogue order.
        dtos = dtos.Where(d => d.Pinned).OrderBy(d => pins.IndexOf(d.Id))
            .Concat(dtos.Where(d => !d.Pinned)).ToList();
        return new LauncherManifestDto(VersionOf(dtos), dtos);
    }

    public async Task<LauncherManifestDto> SetPinsAsync(User user, IReadOnlyList<string>? appIds, CancellationToken ct)
    {
        var allowed = All().Where(a => a.AllowedFor(user)).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var pins = (appIds ?? [])
            .Select(id => id?.Trim().ToLowerInvariant() ?? "")
            .Where(allowed.Contains)
            .Distinct()
            .ToList();
        if (pins.Count > LauncherContract.MaxPins)
            throw new ChatRejectedException($"You can pin up to {LauncherContract.MaxPins} apps.");

        user.PinnedApps = string.Join(",", pins);
        await db.SaveChangesAsync(ct);
        return ManifestFor(user);
    }

    static List<string> PinsOf(User user, IReadOnlyList<SubApp> apps) => user.PinnedApps is { } saved
        ? saved.Split(',', StringSplitOptions.RemoveEmptyEntries).Where(id => apps.Any(a => a.Id == id)).ToList()
        : apps.Where(a => a.DefaultPinned).Select(a => a.Id).ToList();

    static SubApp Merge(SubApp app, SubAppOptions o) => app with
    {
        Title = o.Title ?? app.Title,
        Description = o.Description ?? app.Description,
        Icon = o.Icon ?? app.Icon,
        Route = o.Route ?? app.Route,
        Version = o.Version ?? app.Version,
        MinShellVersion = o.MinShellVersion ?? app.MinShellVersion,
        Order = o.Order ?? app.Order,
        DefaultPinned = o.DefaultPinned ?? app.DefaultPinned,
        Scopes = o.Scopes ?? app.Scopes,
        RequiredRoles = o.RequiredRoles?.Select(r => r.Trim().ToLowerInvariant()).Where(r => r.Length > 0).ToArray() ?? app.RequiredRoles,
        BundleUri = o.BundleUri ?? app.BundleUri,
        Sha256 = o.Sha256 ?? app.Sha256,
        Assembly = o.Assembly ?? app.Assembly
    };

    static string VersionOf(IReadOnlyList<SubAppDto> apps) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(apps, ContractJson.Options))))[..16].ToLowerInvariant();
}

public static class LauncherModule
{
    public static IServiceCollection AddLauncher(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<LauncherOptions>(config.GetSection(LauncherOptions.Section));
        services.AddScoped<LauncherService>();
        services.AddSingleton<SubAppBundles>();
        return services;
    }

    public static void MapLauncher(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + LauncherContract.ManifestPath).RequireAuthorization();

        api.MapGet("", async (HttpContext http, CurrentUserService users, LauncherService launcher) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(launcher.ManifestFor(me));
        });

        api.MapPut("/pins", async (SetPinsRequest request, HttpContext http, CurrentUserService users, LauncherService launcher) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await launcher.SetPinsAsync(me, request.AppIds, http.RequestAborted));
        });
    }

    /// <summary>
    /// Refuses a sub-app's endpoints to users whose roles don't allow it, so hiding it in the
    /// launcher isn't the only thing standing in the way.
    /// </summary>
    public static TBuilder RequireSubApp<TBuilder>(this TBuilder builder, string appId) where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            var me = await http.RequestServices.GetRequiredService<CurrentUserService>().GetAsync(http.User, http.RequestAborted);
            var app = http.RequestServices.GetRequiredService<LauncherService>().Find(appId);
            if (app is null || !app.AllowedFor(me))
                return Results.Problem(
                    title: $"You don't have access to {app?.Title ?? appId}.",
                    statusCode: StatusCodes.Status403Forbidden);
            return await next(context);
        });
}
