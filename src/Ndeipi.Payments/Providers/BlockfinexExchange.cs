namespace Ndeipi.Payments.Providers;

/// <summary>
/// Blockfinex as an exchange, for crypto features only: Ndeipi Points are bought and cashed out with
/// fiat at a fixed price, so on-ramp and off-ramp never call it (configure with Payments:Exchange).
/// Its API (https://exchangedocsv2.gitbook.io/open-api-doc-v2) has no fiat endpoints, which is why
/// fiat runs on <see cref="PayPalRail"/> and <see cref="AbsaRail"/>. If it is used, the adapter has to handle:
///
/// - **Signing:** headers <c>X-CH-APIKEY</c>, <c>X-CH-TS</c> (milliseconds) and <c>X-CH-SIGN</c>,
///   a hex HMAC-SHA256 over <c>ts + METHOD + path + body</c>. Requests older than 5 seconds are
///   refused, so the adapter keeps its clock offset from <c>/sapi/v2/time</c>.
/// - **Conversion:** spot orders (<c>/sapi/v2/order</c>). A duplicate <c>newClientOrderId</c> is
///   not rejected, so before any retry the adapter looks the order up by that ID (SRV-PROV-04).
/// - **On-chain withdrawals:** <c>/sapi/v1/withdraw/apply</c> takes a unique
///   <c>withdrawOrderId</c>, which carries the reference.
/// - **Status:** there are no webhooks, and the private WebSocket covers only balances and orders,
///   so deposit and withdrawal status is polled (SRV-PROV-05). A 504 means the outcome is unknown,
///   not that it failed.
/// - **No test environment:** adapter tests run against recorded responses (SRS §9.1).
///
/// The documentation is unbranded; Blockfinex still has to confirm its base URL.
/// Credentials come from the secrets manager (SRV-PROV-03).
/// </summary>
public sealed class BlockfinexExchange : IExchangeProvider
{
    public string Name => "blockfinex";

    public Task<ProviderRate> GetRateAsync(string from, string to, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderOperation> ConvertAsync(ConversionRequest request, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderOperation?> GetStatusAsync(string reference, CancellationToken ct) => throw Unbuilt();
    public Task<ProviderStatement> GetStatementAsync(DateTimeOffset since, CancellationToken ct) => throw Unbuilt();

    static ProviderUnavailableException Unbuilt() => new("The Blockfinex exchange adapter is not built yet (milestone M4).");
}
