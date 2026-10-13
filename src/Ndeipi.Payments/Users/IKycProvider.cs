namespace Ndeipi.Payments.Users;

/// <summary>
/// The identity-verification provider behind hosted onboarding (SRV-KYC-01). Which provider is an
/// open question (SRS §11); the server needs only these calls from it.
/// </summary>
public interface IKycProvider
{
    /// <summary>A hosted verification link for one user. <paramref name="reference"/> is the user's ID.</summary>
    Task<KycLink> CreateLinkAsync(string reference, string userType, string? redirectUrl, CancellationToken ct);
}

public sealed record KycLink(Uri Url, DateTimeOffset ExpiresAt);

/// <summary>Sandbox and development: links that go nowhere; outcomes are set with the sandbox KYC call (FR-SBX-02).</summary>
public sealed class SimulatedKycProvider(TimeProvider clock) : IKycProvider
{
    public Task<KycLink> CreateLinkAsync(string reference, string userType, string? redirectUrl, CancellationToken ct) =>
        Task.FromResult(new KycLink(new Uri($"https://kyc.sandbox.ndeipi.example/verify/{reference}"), clock.GetUtcNow().AddDays(7)));
}
