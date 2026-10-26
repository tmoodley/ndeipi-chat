using System.Net.Http.Json;
using NdeipiChat.SubApps.Farm.Models;

namespace NdeipiChat.SubApps.Farm.Services;

public record Attestation(bool Approved, double Quantity, string Attestor, string Reference, string Note);

/// <summary>
/// Integration seam between the game and NodeBoss / Ndeipi Enterprise Server.
/// The game only ever talks to this interface.
/// </summary>
public interface INodeBossClient
{
    /// <summary>Attest a livestock registration (identity, ownership, custody).</summary>
    Task<Attestation> AttestAnimalAsync(Animal animal, int day);

    /// <summary>Verify pending sequestration/avoidance and return the quantity to mint as credits.</summary>
    Task<Attestation> VerifyCarbonAsync(double tonnes, IReadOnlyCollection<string> evidence, int nodeCount, int day);

    /// <summary>Daily NDX staking yield as a fraction (0.001 = 0.1%/day).</summary>
    Task<double> GetDailyStakingRateAsync(int nodeCount);
}

/// <summary>Offline simulation so the game is fully playable without a backend.</summary>
public sealed class SimulatedNodeBossClient : INodeBossClient
{
    private const string Registrar = "NACTT registrar via NodeBoss (simulated)";
    private const double BufferPool = 0.10;
    private readonly Random _rng = new();

    public Task<Attestation> AttestAnimalAsync(Animal a, int day)
    {
        var (min, max) = a.Species == Species.Cattle ? (60d, 1200d) : (5d, 150d);

        if (a.TagNumber.Length < 4)
            return Task.FromResult(new Attestation(false, 0, Registrar, "", "Ear tag must be at least 4 characters."));

        if (a.WeightKg < min || a.WeightKg > max)
            return Task.FromResult(new Attestation(false, 0, Registrar, "",
                $"Weight {a.WeightKg:0} kg is outside the plausible range for {a.Species} ({min:0}–{max:0} kg)."));

        return Task.FromResult(new Attestation(true, 1, Registrar, $"NACTT-{day:D4}-{a.TagNumber}",
            "Identity, ownership and custody attested."));
    }

    public Task<Attestation> VerifyCarbonAsync(double tonnes, IReadOnlyCollection<string> evidence, int nodeCount, int day)
    {
        if (nodeCount == 0)
            return Task.FromResult(new Attestation(false, 0, "NodeBoss", "", "No NodeBoss node online to verify."));
        if (evidence.Count == 0)
            return Task.FromResult(new Attestation(false, 0, "NodeBoss", "", "No practice or manure-management evidence on record."));

        var issued = Math.Round(tonnes * (1 - BufferPool), 3);
        var reference = $"NB-CC-{day:D4}-{_rng.Next(0x1000, 0xFFFF):X4}";
        return Task.FromResult(new Attestation(true, issued, $"NodeBoss solar node cluster ×{nodeCount} (simulated)", reference,
            $"{BufferPool:P0} withheld to the buffer pool."));
    }

    public Task<double> GetDailyStakingRateAsync(int nodeCount) =>
        Task.FromResult(nodeCount == 0 ? 0 : 0.0012 * Math.Min(nodeCount, 5));
}

/// <summary>
/// Production client skeleton for Ndeipi Enterprise Server, called through SubAppContext.Api. Endpoint
/// paths are placeholders: add them to the API (behind RequireSubApp) before FarmApp.razor uses this client.
/// </summary>
public sealed class HttpNodeBossClient : INodeBossClient
{
    private readonly HttpClient _http;
    public HttpNodeBossClient(HttpClient http) => _http = http;

    public async Task<Attestation> AttestAnimalAsync(Animal animal, int day)
    {
        var res = await _http.PostAsJsonAsync("api/nodeboss/livestock/attest", new
        {
            animal.Id, animal.TagNumber, Species = animal.Species.ToString(), animal.Name, animal.Breed, animal.WeightKg, day,
        });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<Attestation>())!;
    }

    public async Task<Attestation> VerifyCarbonAsync(double tonnes, IReadOnlyCollection<string> evidence, int nodeCount, int day)
    {
        var res = await _http.PostAsJsonAsync("api/nodeboss/carbon/verify", new { tonnes, evidence, nodeCount, day });
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<Attestation>())!;
    }

    public async Task<double> GetDailyStakingRateAsync(int nodeCount) =>
        await _http.GetFromJsonAsync<double>($"api/nodeboss/staking/rate?nodes={nodeCount}");
}
