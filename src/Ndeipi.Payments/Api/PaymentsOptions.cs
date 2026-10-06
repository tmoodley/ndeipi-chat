namespace Ndeipi.Payments.Api;

public enum PaymentsEnvironment { Sandbox, Production }

public sealed class PaymentsOptions
{
    public const string Section = "Payments";

    /// <summary>
    /// Which environment this deployment is. Keys from the other environment are refused
    /// (FR-CORE-03), sandbox calls answer 403 in production, and production refuses to start with a
    /// simulated provider (SC-06).
    /// </summary>
    public PaymentsEnvironment Environment { get; set; } = PaymentsEnvironment.Sandbox;

    /// <summary>
    /// The fiat rail providers: "simulated" (sandbox), or "paypal" and "absa" (SRV-PROV-01). Each
    /// serves the rail codes it names; two providers may not serve the same code.
    /// </summary>
    // Empty by default: the binder appends configured array items to a default rather than replacing it.
    public string[] FiatRails { get; set; } = [];

    /// <summary>
    /// "none" (the default), "simulated" or "blockfinex". Wallets hold Ndeipi Points bought with fiat
    /// at a fixed price, so ramps never convert through an exchange; this is for crypto features only.
    /// </summary>
    public string Exchange { get; set; } = "none";

    public PointsOptions Points { get; set; } = new();

    /// <summary>Whether any provider is simulated; production refuses to start if so (SC-06).</summary>
    public bool UsesSimulatedProviders => FiatRails.Contains("simulated") || Exchange == "simulated" || KycProvider == "simulated";

    /// <summary>"simulated" until a KYC provider is chosen (SRS §11).</summary>
    public string KycProvider { get; set; } = "simulated";

    /// <summary>How long an idempotency key replays its first response (FR-CORE-04, SRV-LED-07).</summary>
    public TimeSpan IdempotencyWindow { get; set; } = TimeSpan.FromHours(24);

    public RateLimitOptions RateLimit { get; set; } = new();

    /// <summary>Ndeipi Points: the wallet asset, bought and cashed out at a fixed price per currency.</summary>
    public sealed class PointsOptions
    {
        public string Asset { get; set; } = "ndeipi-points";

        /// <summary>Decimal places a points amount may carry.</summary>
        public int Decimals { get; set; } = 2;

        /// <summary>The price of one point, per lower-case ISO 4217 currency: <c>{ "zar": 1.00 }</c>.</summary>
        public Dictionary<string, decimal> Prices { get; set; } = [];
    }

    public CoinOptions Coin { get; set; } = new();

    /// <summary>
    /// NdeipiCoin: the opt-in second asset, converted to and from points at a locked quote. Its price
    /// comes from the last trade the treasury team recorded with Blockfinex's manual OTC desk.
    /// </summary>
    public sealed class CoinOptions
    {
        public string Asset { get; set; } = "ndeipi-coin";

        /// <summary>Decimal places a NdeipiCoin amount may carry in the API (the token itself has 18).</summary>
        public int Decimals { get; set; } = 8;

        /// <summary>Ndeipi's spread on every conversion, in basis points of the amount paid.</summary>
        public int SpreadBasisPoints { get; set; } = 150;

        /// <summary>How long a quote holds its price.</summary>
        public TimeSpan QuoteLifetime { get; set; } = TimeSpan.FromSeconds(30);

        /// <summary>Quotes stop when the last recorded OTC trade is older than this: the price is no longer known.</summary>
        public TimeSpan MaxPriceAge { get; set; } = TimeSpan.FromDays(3);
    }

    public sealed class RateLimitOptions
    {
        /// <summary>Requests per key per window (SRV-OPS-06). Set per integrator later; one default for now.</summary>
        public int PermitLimit { get; set; } = 100;
        public TimeSpan Window { get; set; } = TimeSpan.FromSeconds(1);
    }
}
