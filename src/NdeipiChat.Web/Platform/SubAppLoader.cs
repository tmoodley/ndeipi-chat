using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebAssembly.Services;
using NdeipiChat.Contracts;
using NdeipiChat.SubApps.Sdk;

namespace NdeipiChat.Web.Platform;

public sealed class SubAppLoadException(string message) : Exception(message);

/// <summary>
/// Loads a sub-app on first open (SRS SR-02-02): fetches its bundle, checks it against the
/// manifest's SHA-256 (NFR-02-01), lazy-loads the assembly and finds its root component. Loaded
/// apps are remembered, so opening one again is instant (NFR-01-02).
/// </summary>
public sealed class SubAppLoader(LazyAssemblyLoader lazyLoader, NavigationManager navigation, ILogger<SubAppLoader> log)
{
    readonly Dictionary<string, Type> _loaded = [];
    readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsLoaded(SubAppDto app) => _loaded.ContainsKey(Key(app));

    public async Task<Type> LoadAsync(SubAppDto app)
    {
        if (app.BundleUri is null || app.Assembly is null || app.Sha256 is null)
            throw new SubAppLoadException($"{app.Title} can't be opened in the browser.");

        await _gate.WaitAsync();
        try
        {
            if (_loaded.TryGetValue(Key(app), out var known))
                return known;

            byte[] bundle;
            try
            {
                using var http = new HttpClient { BaseAddress = new Uri(navigation.BaseUri) };
                bundle = await http.GetByteArrayAsync(app.BundleUri);
            }
            catch (HttpRequestException ex)
            {
                log.LogWarning(ex, "Couldn't download {App}", app.Id);
                throw new SubAppLoadException($"Couldn't download {app.Title}. Check your connection and try again.");
            }

            if (!SubAppIntegrity.Matches(bundle, app.Sha256))
            {
                log.LogError("{App} failed its integrity check: expected {Expected}, got {Actual}",
                    app.Id, app.Sha256, SubAppIntegrity.Sha256Hex(bundle));
                throw new SubAppLoadException($"{app.Title} didn't match the version your apps list expects, so it wasn't opened. Reload the page to get the latest.");
            }

            // Blazor fetches the same file again (from the browser cache) and checks it against the
            // hash in its own boot manifest, so what runs is what was checked here.
            var assemblies = await lazyLoader.LoadAssembliesAsync([app.Assembly + ".wasm"]);
            var root = SubAppDiscovery.FindRoot(assemblies, app.Id)
                ?? throw new SubAppLoadException($"{app.Title} is installed but has nothing to show.");
            _loaded[Key(app)] = root;
            return root;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>By id and hash: a new version of the app is a new load.</summary>
    static string Key(SubAppDto app) => $"{app.Id}:{app.Sha256}";
}
