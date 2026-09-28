using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Launcher;

namespace NdeipiChat.Tests;

/// <summary>Picking a sub-app's bundle when a deployment has kept files from earlier releases.</summary>
public sealed class SubAppBundleTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "ndeipi-bundles", Guid.NewGuid().ToString("N"));

    sealed class Env(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "NdeipiChat.Api";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
        public string EnvironmentName { get; set; } = "Production";
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = new PhysicalFileProvider(Path.Combine(root, "wwwroot"));
    }

    sealed class Monitor : IOptionsMonitor<LauncherOptions>
    {
        public LauncherOptions CurrentValue { get; } = new();
        public LauncherOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<LauncherOptions, string?> listener) => null;
    }

    SubAppBundles Bundles()
    {
        return new SubAppBundles(new Env(_root), new Monitor(), NullLogger<SubAppBundles>.Instance);
    }

    void Write(string name, string content, DateTime modified)
    {
        var path = Path.Combine(_root, "wwwroot", "_framework", name);
        File.WriteAllText(path, content);
        File.SetLastWriteTimeUtc(path, modified);
    }

    public SubAppBundleTests() => Directory.CreateDirectory(Path.Combine(_root, "wwwroot", "_framework"));

    [Fact]
    public void The_bundle_the_site_serves_wins_over_older_copies_left_on_the_server()
    {
        // "aaa" sorts first and is newer, but this release serves "zzz".
        Write("NdeipiChat.SubApps.Events.aaaaaaaaaa.wasm", "stale", DateTime.UtcNow);
        Write("NdeipiChat.SubApps.Events.zzzzzzzzzz.wasm", "current", DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(_root, "NdeipiChat.Api.staticwebassets.endpoints.json"),
            """{"Version":1,"Endpoints":[{"Route":"_framework/NdeipiChat.SubApps.Events.zzzzzzzzzz.wasm"},{"Route":"_framework/NdeipiChat.SubApps.Events.wasm"}]}""");

        Assert.Equal("_framework/NdeipiChat.SubApps.Events.zzzzzzzzzz.wasm", Bundles().Find("NdeipiChat.SubApps.Events")!.Uri);
    }

    [Fact]
    public void Without_a_manifest_the_newest_copy_wins()
    {
        Write("NdeipiChat.SubApps.Gigs.aaaaaaaaaa.wasm", "old", DateTime.UtcNow.AddDays(-1));
        Write("NdeipiChat.SubApps.Gigs.bbbbbbbbbb.wasm", "new", DateTime.UtcNow);

        Assert.Equal("_framework/NdeipiChat.SubApps.Gigs.bbbbbbbbbb.wasm", Bundles().Find("NdeipiChat.SubApps.Gigs")!.Uri);
        Assert.Null(Bundles().Find("NdeipiChat.SubApps.Missing"));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
