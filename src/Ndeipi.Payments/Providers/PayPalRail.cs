namespace Ndeipi.Payments.Providers;

/// <summary>
/// PayPal as a fiat rail (milestone M4). The parts of PayPal's API this adapter will use:
///
/// - **Collection:** Orders v2. The payer approves a PayPal order on PayPal's hosted page (the
///   <c>approval_url</c> in <c>paypal</c> deposit instructions), and the server captures it. A
///   standing deposit account has no PayPal equivalent, so the rail serves one-off on-ramps only.
/// - **Payout:** the Payouts API. <c>sender_batch_id</c> carries the transfer's reference, and
///   PayPal rejects a reused ID for 30 days, so a retry cannot pay twice (SRV-PROV-04). The
///   <c>PayPal-Request-Id</c> header is set to the same reference.
/// - **Status:** PayPal webhooks, verified with PayPal's signature check, with polling as the
///   fallback (SRV-PROV-05).
/// - **Sandbox:** PayPal has one, so this adapter's tests can run against it (SRS §9.1).
///
/// PayPal's Acceptable Use Policy needs pre-approval for payments involving "any digital
/// representation of value that can be digitally traded, transferred, or used for payment",
/// virtual in-game currencies included. Ndeipi Points, which users send and spend, most likely
/// qualify, so that approval is a launch dependency (LEG-07). Credentials come from the secrets
/// manager (SRV-PROV-03).
/// </summary>
public sealed class PayPalRail : IFiatRail
{
    public string Name => "paypal";

    public IReadOnlyCollection<string> Rails { get; } = [RailCodes.PayPal];

    public Task<FiatCollection> StartCollectionAsync(FiatCollectionRequest request, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderOperation> SubmitPayoutAsync(FiatPayoutRequest request, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderOperation?> GetStatusAsync(string reference, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderStatement> GetStatementAsync(DateTimeOffset since, CancellationToken ct) => throw Unbuilt();

    static ProviderUnavailableException Unbuilt() => new("The PayPal rail is not built yet (milestone M4).");
}
