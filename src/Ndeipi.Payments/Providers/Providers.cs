namespace Ndeipi.Payments.Providers;

/// <summary>
/// The providers behind the adapter layer (SRS §4.12), in Ndeipi's vocabulary only: each
/// implementation maps its provider's identifiers, fields and errors to these (SRV-PROV-02), and
/// nothing outside <c>Providers/</c> knows which provider is in use (SC-03, DC-08).
///
/// Two kinds of provider:
/// - <see cref="IFiatRail"/>: collects and pays out fiat. Absa Bank and PayPal. Fiat becomes
///   Ndeipi Points, and back, at a fixed price (<c>Ramps/PointsPricing</c>).
/// - <see cref="IExchangeProvider"/>: optional, for crypto features only. Blockfinex. No ramp
///   calls it.
///
/// The fiat sits in Ndeipi's own balance at each rail, tracked by the ledger's clearing accounts
/// (docs/payments-api/README.md, "Providers and Ndeipi Points").
///
/// Every call carries a server-generated <c>reference</c> unique to the request. Where a provider
/// has no idempotency of its own, the adapter calls <c>GetStatusAsync</c> with that reference
/// before any retry (SRV-PROV-04).
/// </summary>
public static class RailCodes
{
    /// <summary>A PayPal account, by email or phone.</summary>
    public const string PayPal = "paypal";

    /// <summary>An Absa Bank account, paid in and out by electronic funds transfer.</summary>
    public const string AbsaEft = "absa_eft";
}

/// <summary>Collects and pays out fiat on one or more rails (FR-ON-01, FR-ON-02, FR-OFF-03).</summary>
public interface IFiatRail
{
    /// <summary>For logs, the operator console and the ledger's clearing accounts; never sent to integrators.</summary>
    string Name { get; }

    /// <summary>The rail codes this provider serves, as <c>GET /routes</c> publishes them.</summary>
    IReadOnlyCollection<string> Rails { get; }

    /// <summary>
    /// Instructions for paying in: standing details when <see cref="FiatCollectionRequest.Amount"/> is
    /// null, or one expected payment.
    /// </summary>
    Task<FiatCollection> StartCollectionAsync(FiatCollectionRequest request, CancellationToken ct);

    Task<ProviderOperation> SubmitPayoutAsync(FiatPayoutRequest request, CancellationToken ct);

    /// <summary>Where an operation stands, by the server's own reference; null if the provider never saw it.</summary>
    Task<ProviderOperation?> GetStatusAsync(string reference, CancellationToken ct);

    /// <summary>Balances and movements since a time, for daily reconciliation (SRV-PROV-06).</summary>
    Task<ProviderStatement> GetStatementAsync(DateTimeOffset since, CancellationToken ct);
}

/// <summary>Converts between assets and holds crypto, for crypto features only; ramps never call it.</summary>
public interface IExchangeProvider
{
    string Name { get; }

    Task<ProviderRate> GetRateAsync(string from, string to, CancellationToken ct);

    Task<ProviderOperation> ConvertAsync(ConversionRequest request, CancellationToken ct);

    Task<ProviderOperation?> GetStatusAsync(string reference, CancellationToken ct);

    Task<ProviderStatement> GetStatementAsync(DateTimeOffset since, CancellationToken ct);
}

/// <summary>A provider is down or not built: ramp requests are then refused with <c>provider_unavailable</c>, never dropped (SRV-PROV-07).</summary>
public sealed class ProviderUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record FiatCollectionRequest(
    string Reference,
    string Rail,
    string Currency,
    decimal? Amount,
    string? PayerEmail,
    Uri? ReturnUrl);

/// <summary>
/// How the payer pays: <c>bank</c> details to transfer to, or a <c>paypal</c> approval link to open.
/// Details keys follow the contract's <c>DepositInstructions</c> fields.
/// </summary>
public sealed record FiatCollection(
    string ProviderReference,
    string Type,
    IReadOnlyDictionary<string, string> Details,
    DateTimeOffset? ExpiresAt);

public sealed record FiatPayoutRequest(
    string Reference,
    string Rail,
    string Currency,
    decimal Amount,
    string Country,
    string AccountOwnerName,
    IReadOnlyDictionary<string, string> Destination);

public sealed record ConversionRequest(string Reference, string From, string To, decimal Amount);

public sealed record ProviderRate(string From, string To, decimal Rate, DateTimeOffset AsOf);

public enum ProviderOperationStatus { Pending, Submitted, Completed, Returned, Undeliverable, Failed }

public sealed record ProviderOperation(
    string Reference,
    string ProviderReference,
    ProviderOperationStatus Status,
    decimal? AmountIn,
    decimal? AmountOut,
    decimal? Rate,
    decimal? Fee,
    string? RailReference,
    string? Reason);

public sealed record ProviderMovement(string? Reference, string Asset, decimal Amount, DateTimeOffset At);

/// <summary>What one provider says Ndeipi holds there, per asset or currency, and what moved.</summary>
public sealed record ProviderStatement(
    string Provider,
    IReadOnlyDictionary<string, decimal> Balances,
    IReadOnlyList<ProviderMovement> Movements,
    DateTimeOffset AsOf);
