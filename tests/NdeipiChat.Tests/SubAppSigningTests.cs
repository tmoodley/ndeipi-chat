using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using NdeipiChat.Client;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Contracts;
using NdeipiChat.SubAppSigner;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Signed sub-apps (SRS NFR-02-01): the publisher signs bundles off the server, the API only passes
/// the signatures on, and the shell runs a bundle only if a key it trusts signed exactly that bundle.
/// </summary>
public sealed class SubAppSigningTests(TestApp app) : IClassFixture<TestApp>, IDisposable
{
    const string Inventory = "NdeipiChat.SubApps.Inventory";

    readonly string _release = Path.Combine(Path.GetTempPath(), "ndeipi-signing", Guid.NewGuid().ToString("N"));
    readonly (string PrivateKeyPem, string PublicKeySpki) _publisher = SubAppBundleSigner.CreateKey();
    readonly (string PrivateKeyPem, string PublicKeySpki) _stranger = SubAppBundleSigner.CreateKey();

    public void Dispose()
    {
        if (Directory.Exists(_release))
            Directory.Delete(_release, recursive: true);
    }

    /// <summary>The bundle the site serves, laid out as a published site, as the release step signs it.</summary>
    async Task<(byte[] Bundle, string SiteRoot)> PublishedSiteAsync()
    {
        var user = await app.CreateUserAsync("Release Manager");
        var inventory = (await user.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath)).Apps.Single(a => a.Id == InventoryContract.AppId);
        using var browser = app.CreateClient();
        var bundle = await browser.GetByteArrayAsync(inventory.BundleUri);

        var site = Path.Combine(_release, "wwwroot");
        Directory.CreateDirectory(Path.Combine(site, "_framework"));
        await File.WriteAllBytesAsync(Path.Combine(site, "_framework", Path.GetFileName(inventory.BundleUri!)), bundle);
        await File.WriteAllBytesAsync(Path.Combine(site, "_framework", "NdeipiChat.SubApps.Sdk.a1b2c3d4e5.wasm"), [1, 2, 3]);
        return (bundle, site);
    }

    async Task<SubAppDto> InventoryInManifestAsync(SubAppSignatureFile signatures)
    {
        var path = Path.Combine(_release, "subapp-signatures.json");
        await File.WriteAllTextAsync(path, SubAppBundleSigner.ToJson(signatures));
        await using var server = app.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Launcher:SignaturesPath"] = path })));
        var user = await app.CreateUserAsync("Signed Viewer");
        using var client = server.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create(user.ClerkId, user.SessionId));
        var manifest = await client.GetFromJsonAsync<LauncherManifestDto>(LauncherContract.ManifestPath, ContractJson.Options);
        return manifest!.Apps.Single(a => a.Id == InventoryContract.AppId);
    }

    static SubAppVerifier Shell(bool requireSignature, params string[] trustedKeys) =>
        new(SubAppTrust.Of(requireSignature, trustedKeys), new DotNetP256Signer());

    [Fact]
    public async Task A_bundle_signed_by_a_trusted_publisher_runs()
    {
        var (bundle, site) = await PublishedSiteAsync();
        var signatures = SubAppBundleSigner.SignSite(site, _publisher.PrivateKeyPem);

        // The Sdk ships with the shell; only sub-apps are signed.
        var signed = Assert.Single(signatures.Signatures);
        Assert.Equal((Inventory, SubAppSigning.KeyId(_publisher.PublicKeySpki)), (signed.Assembly, signed.KeyId));

        var inventory = await InventoryInManifestAsync(signatures);
        Assert.Equal((signed.KeyId, signed.Signature), (inventory.SigningKeyId, inventory.Signature));
        Assert.Null(await Shell(true, _publisher.PublicKeySpki).ProblemWithAsync(inventory, bundle));
    }

    [Fact]
    public async Task The_shell_refuses_what_it_cant_trust()
    {
        var (bundle, site) = await PublishedSiteAsync();
        var inventory = await InventoryInManifestAsync(SubAppBundleSigner.SignSite(site, _publisher.PrivateKeyPem));
        var shell = Shell(true, _publisher.PublicKeySpki);

        // Changed after signing: the hash no longer matches.
        Assert.Contains("didn't match the version", await shell.ProblemWithAsync(inventory, [.. bundle, 0]));
        // Signed, but by a key this shell doesn't know.
        Assert.Contains("doesn't trust", await Shell(true, _stranger.PublicKeySpki).ProblemWithAsync(inventory, bundle));
        // A signature that isn't the publisher's, relabelled with the publisher's key id.
        var forged = SubAppBundleSigner.SignSite(site, _stranger.PrivateKeyPem).Signatures.Single();
        Assert.Contains("signature doesn't match", await shell.ProblemWithAsync(inventory with { Signature = forged.Signature }, bundle));
        // The publisher's signature, claimed for a different assembly.
        Assert.Contains("signature doesn't match", await shell.ProblemWithAsync(inventory with { Assembly = "NdeipiChat.SubApps.Trading" }, bundle));
        // Garbage instead of a signature.
        Assert.Contains("signature doesn't match", await shell.ProblemWithAsync(inventory with { Signature = "not-base64!" }, bundle));
    }

    [Fact]
    public async Task Unsigned_bundles_run_only_where_signatures_arent_required()
    {
        var (bundle, _) = await PublishedSiteAsync();
        var inventory = await InventoryInManifestAsync(new SubAppSignatureFile([]));
        Assert.Equal(((string?)null, (string?)null), (inventory.SigningKeyId, inventory.Signature));

        Assert.Contains("isn't signed", await Shell(true, _publisher.PublicKeySpki).ProblemWithAsync(inventory, bundle));
        Assert.Null(await Shell(false).ProblemWithAsync(inventory, bundle));
    }

    [Fact]
    public async Task A_signature_from_an_earlier_release_isnt_passed_on()
    {
        var (_, site) = await PublishedSiteAsync();
        var stale = SubAppBundleSigner.SignSite(site, _publisher.PrivateKeyPem).Signatures.Single() with { Sha256 = new string('0', 64) };

        var inventory = await InventoryInManifestAsync(new SubAppSignatureFile([stale]));

        Assert.Null(inventory.Signature);
    }

    [Fact]
    public void The_payload_binds_the_assembly_and_the_exact_hash()
    {
        var hash = new string('a', 64);
        Assert.Equal(SubAppSigning.Payload(Inventory, hash), SubAppSigning.Payload(Inventory, hash.ToUpperInvariant()));
        Assert.NotEqual(SubAppSigning.Payload(Inventory, hash), SubAppSigning.Payload("NdeipiChat.SubApps.Trading", hash));
        Assert.NotEqual(SubAppSigning.Payload(Inventory, hash), SubAppSigning.Payload(Inventory, new string('b', 64)));
        Assert.Equal(16, SubAppSigning.KeyId(_publisher.PublicKeySpki).Length);
    }
}
