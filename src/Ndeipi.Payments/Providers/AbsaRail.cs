namespace Ndeipi.Payments.Providers;

/// <summary>
/// Absa Bank as a fiat rail (milestone M4): standing and one-off deposits into Ndeipi's Absa
/// account, matched by payment reference, and EFT payouts from it.
///
/// Absa offers its corporate clients APIs and host-to-host file exchange through Absa Access. Its
/// API documentation is not public, so the interface used here, the payment-reference format,
/// statement access, idempotency on payouts and the supported countries all wait on onboarding
/// with Absa Corporate and Investment Banking. Until then this adapter is a placeholder.
/// Credentials come from the secrets manager (SRV-PROV-03).
/// </summary>
public sealed class AbsaRail : IFiatRail
{
    public string Name => "absa";

    public IReadOnlyCollection<string> Rails { get; } = [RailCodes.AbsaEft];

    public Task<FiatCollection> StartCollectionAsync(FiatCollectionRequest request, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderOperation> SubmitPayoutAsync(FiatPayoutRequest request, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderOperation?> GetStatusAsync(string reference, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderStatement> GetStatementAsync(DateTimeOffset since, CancellationToken ct) => throw Unbuilt();

    static ProviderUnavailableException Unbuilt() => new("The Absa rail is not built yet (milestone M4, after Absa onboarding).");
}
