using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class LauncherTests(TestApp app) : IClassFixture<TestApp>
{
    /// <summary>The same API and database, with extra launcher settings.</summary>
    WebApplicationFactory<Program> Configured(Dictionary<string, string?> settings) =>
        app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(settings)));

    static HttpClient ClientFor(WebApplicationFactory<Program> server, TestUser user)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user.ClerkId, user.SessionId));
        return client;
    }

    [Fact]
    public async Task Everyone_gets_the_built_in_sub_apps_with_the_default_pins()
    {
        var user = await app.CreateUserAsync("Launcher Default");

        var manifest = await user.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);

        // Built-in apps, then Events, Gigs and Inventory, which appsettings.json adds as runtime-loaded sub-apps.
        Assert.Equal([BuiltInApps.Chats, BuiltInApps.Feed, BuiltInApps.Shamwaris, BuiltInApps.Herd, BuiltInApps.Wallet, EventsContract.AppId, GigsContract.AppId, InventoryContract.AppId], manifest.Apps.Select(a => a.Id));
        Assert.Equal([true, true, true, true, false, false, false, false], manifest.Apps.Select(a => a.Pinned));
        var herd = manifest.Apps.Single(a => a.Id == BuiltInApps.Herd);
        Assert.Equal(("Herd", "herd", "1.0.0", (string?)null, (string?)null), (herd.Title, herd.Route, herd.Version, herd.BundleUri, herd.Sha256));
        Assert.Contains("livestock", herd.Scopes);
        Assert.Equal(manifest.Version, (await user.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath)).Version);
    }

    [Fact]
    public async Task Pins_are_kept_in_the_order_chosen_and_unknown_apps_are_ignored()
    {
        var user = await app.CreateUserAsync("Launcher Pinner");
        var before = await user.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);

        using var response = await user.Http.PutAsJsonAsync(LauncherContract.PinsPath,
            new SetPinsRequest([BuiltInApps.Wallet, "no-such-app", BuiltInApps.Chats, BuiltInApps.Wallet]), ContractJson.Options);
        var pinned = (await response.Content.ReadFromJsonAsync<LauncherManifestDto>(ContractJson.Options))!;

        Assert.Equal([BuiltInApps.Wallet, BuiltInApps.Chats], pinned.Apps.Where(a => a.Pinned).Select(a => a.Id));
        Assert.Equal(BuiltInApps.Wallet, pinned.Apps[0].Id);
        Assert.NotEqual(before.Version, pinned.Version);
        Assert.Equal(pinned.Version, (await user.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath)).Version);
    }

    [Fact]
    public async Task Roles_decide_which_sub_apps_a_user_sees_and_can_call()
    {
        await using var server = Configured(new()
        {
            ["Launcher:Apps:herd:RequiredRoles:0"] = "farmer",
            ["Launcher:Apps:trading:Title"] = "Trading Terminal",
            ["Launcher:Apps:trading:Route"] = "trading",
            ["Launcher:Apps:trading:RequiredRoles:0"] = "Trader"
        });
        var member = await app.CreateUserAsync("Plain Member");
        var farmer = await app.CreateUserAsync("Role Farmer", roles: ["farmer"]);
        var trader = await app.CreateUserAsync("Role Trader", roles: ["trader", "farmer"]);

        async Task<string[]> AppsOf(TestUser user) =>
            (await ClientFor(server, user).GetFromJsonAsync<LauncherManifestDto>(LauncherContract.ManifestPath, ContractJson.Options))!
                .Apps.Select(a => a.Id).ToArray();

        Assert.DoesNotContain(BuiltInApps.Herd, await AppsOf(member));
        Assert.Contains(BuiltInApps.Herd, await AppsOf(farmer));
        Assert.DoesNotContain("trading", await AppsOf(farmer));
        Assert.Contains("trading", await AppsOf(trader));

        // Hidden isn't the only protection: the herd's API refuses the member.
        using var refused = await ClientFor(server, member).GetAsync("api/v1/livestock/cows");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("You don't have access to Herd.", (await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
        using var allowed = await ClientFor(server, farmer).GetAsync("api/v1/livestock/cows");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // Pinning an app you can't have does nothing.
        using var pin = await ClientFor(server, member).PutAsJsonAsync(LauncherContract.PinsPath, new SetPinsRequest([BuiltInApps.Herd, BuiltInApps.Chats]), ContractJson.Options);
        var pinned = (await pin.Content.ReadFromJsonAsync<LauncherManifestDto>(ContractJson.Options))!;
        Assert.Equal([BuiltInApps.Chats], pinned.Apps.Where(a => a.Pinned).Select(a => a.Id));
    }

    [Fact]
    public async Task A_disabled_sub_app_disappears_and_its_API_closes()
    {
        await using var server = Configured(new() { ["Launcher:Apps:wallet:Enabled"] = "false" });
        var user = await app.CreateUserAsync("Wallet Gone");
        var client = ClientFor(server, user);

        var manifest = await client.GetFromJsonAsync<LauncherManifestDto>(LauncherContract.ManifestPath, ContractJson.Options);
        Assert.DoesNotContain(manifest!.Apps, a => a.Id == BuiltInApps.Wallet);
        using var banking = await client.GetAsync("api/banking/status");
        Assert.Equal(HttpStatusCode.Forbidden, banking.StatusCode);
    }
}
