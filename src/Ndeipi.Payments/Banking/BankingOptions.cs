namespace Ndeipi.Payments.Banking;

/// <summary>
/// Ndeipi's own business banking (SRS-NDE-MEOW-001), at <c>Payments:Banking</c>. Off unless a
/// provider is set. Keys and secrets come from user-secrets or the environment, never from
/// appsettings files in the repository.
/// </summary>
public sealed class BankingOptions
{
    /// <summary>"none" (the default), "simulated" (sandbox and tests) or "meow".</summary>
    public string Provider { get; set; } = "none";

    public bool Enabled => Provider is not "none";

    public MeowOptions Meow { get; set; } = new();

    /// <summary>
    /// Payments of at least this many US dollars wait for other officers (FR-3.3). The SRS's example
    /// is $10,000.
    /// </summary>
    public decimal ApprovalThreshold { get; set; } = 10_000m;

    /// <summary>How many officers other than the initiator must approve a payment at or over the threshold.</summary>
    public int RequiredApprovals { get; set; } = 1;

    /// <summary>A payment not approved within this long expires and is never sent.</summary>
    public TimeSpan ApprovalLifetime { get; set; } = TimeSpan.FromHours(72);

    /// <summary>Balances younger than this are served from the cache without asking the bank.</summary>
    public TimeSpan BalanceFreshness { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How often the worker processes bank events and retries dispatches.</summary>
    public TimeSpan WorkerInterval { get; set; } = TimeSpan.FromSeconds(2);

    public SanctionsOptions Sanctions { get; set; } = new();

    public AssistantOptions Assistant { get; set; } = new();

    public sealed class MeowOptions
    {
        /// <summary>The REST API: sandbox by default, <c>https://api.meow.com/v1</c> in production.</summary>
        public string BaseUrl { get; set; } = "https://api.sandbox.meow.com/v1";

        /// <summary>A Meow API key, scoped to Ndeipi's business.</summary>
        public string ApiKey { get; set; } = "";

        /// <summary>Scopes requests to one business when the key reaches several.</summary>
        public string? EntityId { get; set; }

        /// <summary>The <c>whsec_</c> secret Meow returned when the webhook subscription was created.</summary>
        public string WebhookSecret { get; set; } = "";

        /// <summary>Meow's MCP server for API-key clients, used read-only by the assistant (FR-5.1).</summary>
        public string McpUrl { get; set; } = "https://mcp.meow.com/cli";

        /// <summary>
        /// Whether officers may reveal a virtual card's number (once per card). Off by default:
        /// Meow must enable it for the account, and the number passes through this server on its
        /// way to the officer, which puts the server in PCI scope for that moment.
        /// </summary>
        public bool AllowCardReveal { get; set; }
    }

    public sealed class SanctionsOptions
    {
        /// <summary>
        /// OFAC's SDN list as published at treasury.gov (<c>sdn.csv</c>), refreshed by deployment.
        /// Names come from it, and digital currency addresses from its remarks.
        /// </summary>
        public string? SdnCsvPath { get; set; }

        /// <summary>Extra names to refuse, for tests and for lists compliance adds by hand.</summary>
        public string[] Names { get; set; } = [];

        /// <summary>Extra wallet addresses to refuse.</summary>
        public string[] Addresses { get; set; } = [];
    }

    public sealed class AssistantOptions
    {
        /// <summary>An Anthropic API key; the assistant is off without one.</summary>
        public string ApiKey { get; set; } = "";

        public string Model { get; set; } = "claude-opus-5-5";

        /// <summary>Tool calls the assistant may make in one reply before it must answer.</summary>
        public int MaxToolRounds { get; set; } = 8;
    }
}
