using NdeipiChat.Contracts;
using NdeipiChat.SubApps.Farm.Models;
using NdeipiChat.SubApps.Farm.Services;
using NdeipiChat.SubApps.Sdk;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>Ndeipi.City, the farm game: it's in the launcher, and its engine plays and saves on the device.</summary>
public sealed class FarmTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public async Task The_launcher_offers_the_farm_game_with_its_published_bundle()
    {
        var user = await app.CreateUserAsync("Farm Player");

        var manifest = await user.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);

        var farm = manifest.Apps.Single(a => a.Id == FarmContract.AppId);
        Assert.Equal(("Ndeipi.City", "apps/farm", "NdeipiChat.SubApps.Farm"), (farm.Title, farm.Route, farm.Assembly));
        Assert.StartsWith("_framework/NdeipiChat.SubApps.Farm", farm.BundleUri);
    }

    [Fact]
    public async Task A_registered_cow_is_attested_at_the_next_day_and_gets_a_cattle_bond()
    {
        using var game = new FarmGameService(new SimulatedNodeBossClient(), new FarmStorage(new MemoryDevice()));
        await game.InitAsync();
        var kraal = game.State.Tiles.Single(t => t.Kind == TileKind.Kraal).Index;

        var registered = game.RegisterAnimal(kraal, new AnimalRegistration { Name = "Kandondo", TagNumber = "zm-nam-001", WeightKg = 350 });
        Assert.True(registered.Ok, registered.Message);
        Assert.Equal(VerificationStatus.Pending, game.State.Animals.Single().Status);

        await game.AdvanceDayAsync();

        var cow = game.State.Animals.Single();
        Assert.Equal((VerificationStatus.Verified, "ZM-NAM-001"), (cow.Status, cow.TagNumber));
        Assert.StartsWith("CB-ZM-NAM-001-", cow.CattleBondRef);
        Assert.True(SousveillanceLedger.Validate(game.State.Ledger));
    }

    [Fact]
    public async Task Editing_ledger_history_breaks_the_chain()
    {
        using var game = new FarmGameService(new SimulatedNodeBossClient(), new FarmStorage(new MemoryDevice()));
        await game.InitAsync();
        game.Build(0, TileKind.Pasture);
        Assert.True(SousveillanceLedger.Validate(game.State.Ledger));

        game.State.Ledger[0].Payload = "Farm opened by someone else";

        Assert.False(SousveillanceLedger.Validate(game.State.Ledger));
    }

    [Fact]
    public async Task The_farm_is_saved_on_the_device_and_reopens_where_it_was_left()
    {
        var device = new MemoryDevice();
        using (var first = new FarmGameService(new SimulatedNodeBossClient(), new FarmStorage(device)))
        {
            await first.InitAsync();
            Assert.True(first.Build(0, TileKind.CompostPit).Ok);
            await first.AdvanceDayAsync();
        }
        Assert.NotNull(await device.GetValueAsync(FarmContract.SaveKey));

        using var reopened = new FarmGameService(new SimulatedNodeBossClient(), new FarmStorage(device));
        await reopened.InitAsync();

        Assert.Equal((TileKind.CompostPit, 2), (reopened.State.Tiles[0].Kind, reopened.State.Day));
    }

    [Fact]
    public async Task Without_device_storage_the_game_still_plays()
    {
        using var game = new FarmGameService(new SimulatedNodeBossClient(), new FarmStorage(null));
        await game.InitAsync();

        Assert.True(game.HarvestForage(game.State.Tiles.Single(t => t.Kind == TileKind.Pasture).Index).Ok);
    }

    /// <summary>The shell's device storage, in memory. The farm only uses its values.</summary>
    sealed class MemoryDevice : ISubAppDevice
    {
        readonly Dictionary<string, string> _values = new();

        public string DeviceName => "Test device";
        public Task<string> CreateKeyAsync(string name) => throw new NotSupportedException();
        public Task<string?> GetPublicKeyAsync(string name) => throw new NotSupportedException();
        public Task<string?> SignAsync(string name, byte[] payload) => throw new NotSupportedException();
        public Task<bool> VerifyAsync(string publicKeySpki, byte[] payload, string signature) => throw new NotSupportedException();
        public Task<string?> GetValueAsync(string name) => Task.FromResult(_values.GetValueOrDefault(name));

        public Task SetValueAsync(string name, string? value)
        {
            if (value is null) _values.Remove(name);
            else _values[name] = value;
            return Task.CompletedTask;
        }
    }
}
