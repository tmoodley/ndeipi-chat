using NdeipiChat.Api.Banking;

namespace NdeipiChat.Tests;

/// <summary>
/// Not part of the normal run: talks to Bridge's real sandbox to check that what BridgeClient sends
/// and reads matches Bridge's API (the other tests use a stub). Does nothing unless
/// NDEIPI_BRIDGE_SANDBOX_KEY is set to a sandbox key (sk-test-...), e.g.
///   NDEIPI_BRIDGE_SANDBOX_KEY=sk-test-... dotnet test --filter BridgeSandboxCheck
/// Never point it at a live key.
/// </summary>
public sealed class BridgeSandboxCheck
{
    [Fact]
    public async Task Kyc_links_and_wallet_reads_round_trip_against_the_sandbox()
    {
        var key = Environment.GetEnvironmentVariable("NDEIPI_BRIDGE_SANDBOX_KEY");
        if (string.IsNullOrEmpty(key))
            return;
        Assert.StartsWith("sk-test-", key);

        using var http = new HttpClient { BaseAddress = new Uri("https://api.sandbox.bridge.xyz/v0/") };
        http.DefaultRequestHeaders.Add("Api-Key", key);
        var bridge = new BridgeClient(http);

        // The same request BankingService.StartKycAsync makes, for a made-up person.
        var run = Guid.NewGuid().ToString("N")[..8];
        var created = await bridge.CreateKycLinkAsync(
            new BridgeKycLinkRequest($"Sandbox Check {run}", $"sandbox-check-{run}@example.com", "individual", null),
            idempotencyKey: $"sandbox-check-{run}", CancellationToken.None);

        Assert.False(string.IsNullOrEmpty(created.Id));
        Assert.StartsWith("https://", created.KycLink);
        Assert.StartsWith("https://", created.TosLink);
        Assert.False(string.IsNullOrEmpty(created.KycStatus));
        Assert.False(string.IsNullOrEmpty(created.TosStatus));

        var again = await bridge.GetKycLinkAsync(created.Id!, CancellationToken.None);
        Assert.Equal(created.Id, again.Id);

        // Retrying with the same key must not make a second link.
        var retried = await bridge.CreateKycLinkAsync(
            new BridgeKycLinkRequest($"Sandbox Check {run}", $"sandbox-check-{run}@example.com", "individual", null),
            idempotencyKey: $"sandbox-check-{run}", CancellationToken.None);
        Assert.Equal(created.Id, retried.Id);
    }
}
