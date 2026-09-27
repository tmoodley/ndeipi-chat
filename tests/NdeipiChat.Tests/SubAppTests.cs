using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NdeipiChat.Contracts;
using NdeipiChat.SubApps.Inventory;
using NdeipiChat.SubApps.Sdk;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>Sub-apps loaded at runtime (SRS SR-02): the manifest's bundle and hash, the SDK, and the Inventory sub-app's API.</summary>
public sealed class SubAppTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task The_manifest_names_the_published_bundle_and_its_exact_hash()
    {
        var user = await app.CreateUserAsync("Bundle Checker");

        var manifest = await user.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);
        var inventory = manifest.Apps.Single(a => a.Id == InventoryContract.AppId);
        Assert.Equal(("apps/inventory", "NdeipiChat.SubApps.Inventory"), (inventory.Route, inventory.Assembly));
        Assert.StartsWith("_framework/NdeipiChat.SubApps.Inventory", inventory.BundleUri);
        Assert.EndsWith(".wasm", inventory.BundleUri);

        // What the shell does before loading it: download the bundle the site serves, and check it.
        using var browser = app.CreateClient();
        var bundle = await browser.GetByteArrayAsync(inventory.BundleUri);
        Assert.True(SubAppIntegrity.Matches(bundle, inventory.Sha256));
        Assert.False(SubAppIntegrity.Matches([.. bundle, 0], inventory.Sha256));

        // Built-in apps ship with the shell: nothing to download.
        var chats = manifest.Apps.Single(a => a.Id == BuiltInApps.Chats);
        Assert.Equal(((string?)null, (string?)null, (string?)null), (chats.BundleUri, chats.Sha256, chats.Assembly));
    }

    [Fact]
    public async Task A_sub_app_whose_bundle_isnt_published_is_left_out()
    {
        await using var server = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Launcher:Apps:ghost:Title"] = "Ghost",
            ["Launcher:Apps:ghost:Route"] = "apps/ghost",
            ["Launcher:Apps:ghost:Assembly"] = "NdeipiChat.SubApps.Ghost"
        })));
        var user = await app.CreateUserAsync("Ghost Hunter");
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user.ClerkId, user.SessionId));

        var manifest = await client.GetFromJsonAsync<LauncherManifestDto>(LauncherContract.ManifestPath, ContractJson.Options);

        Assert.DoesNotContain(manifest!.Apps, a => a.Id == "ghost");
        Assert.Contains(manifest.Apps, a => a.Id == InventoryContract.AppId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("zz00000000000000000000000000000000000000000000000000000000000000")]
    public void A_bundle_never_matches_a_missing_or_malformed_hash(string? hash) =>
        Assert.False(SubAppIntegrity.Matches([1, 2, 3], hash));

    [Fact]
    public void The_shell_finds_a_sub_apps_root_component_by_its_id()
    {
        var assembly = typeof(InventoryApp).Assembly;
        Assert.Equal(typeof(InventoryApp), SubAppDiscovery.FindRoot([assembly], InventoryContract.AppId));
        Assert.Equal(typeof(InventoryApp), SubAppDiscovery.FindRoot([assembly], "INVENTORY"));
        Assert.Null(SubAppDiscovery.FindRoot([assembly], "trading"));
    }

    [Fact]
    public async Task Inventory_keeps_each_users_stock_to_themselves()
    {
        var alice = await app.CreateUserAsync("Alice Stock");
        var bob = await app.CreateUserAsync("Bob Stock");

        var seed = await alice.PostAsync<InventoryItemDto>(InventoryContract.ItemsPath, new SaveInventoryItemRequest("  Maize seed 10 kg ", "MZ-10", "Shed 2", 12));
        Assert.Equal(("Maize seed 10 kg", "MZ-10", "Shed 2", 12), (seed.Name, seed.Sku, seed.Location, seed.Quantity));

        using (var update = await alice.Http.PutAsJsonAsync($"{InventoryContract.ItemsPath}/{seed.Id}", new SaveInventoryItemRequest(seed.Name, seed.Sku, "", 9), ContractJson.Options))
        {
            var saved = (await update.Content.ReadFromJsonAsync<InventoryItemDto>(ContractJson.Options))!;
            Assert.Equal((9, (string?)null), (saved.Quantity, saved.Location));
        }

        Assert.Empty(await bob.GetAsync<List<InventoryItemDto>>(InventoryContract.ItemsPath));
        using (var steal = await bob.Http.PutAsJsonAsync($"{InventoryContract.ItemsPath}/{seed.Id}", new SaveInventoryItemRequest("Mine now", null, null, 0), ContractJson.Options))
            Assert.Equal(HttpStatusCode.NotFound, steal.StatusCode);
        using (await bob.Http.DeleteAsync($"{InventoryContract.ItemsPath}/{seed.Id}"))
        {
        }
        Assert.Equal(9, Assert.Single(await alice.GetAsync<List<InventoryItemDto>>(InventoryContract.ItemsPath)).Quantity);

        using (await alice.Http.DeleteAsync($"{InventoryContract.ItemsPath}/{seed.Id}"))
        {
        }
        Assert.Empty(await alice.GetAsync<List<InventoryItemDto>>(InventoryContract.ItemsPath));
    }

    [Theory]
    [InlineData("", 1, "Give the item a name")]
    [InlineData("Seed", -1, "Quantity must be between")]
    public async Task Bad_items_are_refused_with_a_reason(string name, int quantity, string reason)
    {
        var user = await app.CreateUserAsync("Careless Stocker");
        using var response = await user.Http.PostAsJsonAsync(InventoryContract.ItemsPath, new SaveInventoryItemRequest(name, null, null, quantity), ContractJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(reason, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Inventory_is_closed_to_users_whose_roles_dont_allow_it()
    {
        await using var server = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Launcher:Apps:inventory:RequiredRoles:0"] = "storekeeper"
        })));
        var clerk = await app.CreateUserAsync("No Store Role");
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(clerk.ClerkId, clerk.SessionId));

        using var response = await client.GetAsync(InventoryContract.ItemsPath);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
