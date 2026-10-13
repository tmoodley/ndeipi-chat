using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Banking.Assistant;

/// <summary>
/// Meow's MCP server, read-only (FR-5.1, FR-5.2). Ndeipi connects with its API key on Meow's
/// API-key surface and passes the assistant only the tools on <see cref="ReadOnly"/>: balances,
/// transactions, transfers, contacts and cards. Meow's write tools (transfers, cards, bills) are
/// never offered, so the assistant can draft a payment only through Ndeipi's own
/// <c>propose_payment</c>, which waits for the officer.
/// </summary>
public sealed class MeowMcpTools(IOptions<PaymentsOptions> options, ILoggerFactory logs) : IAsyncDisposable
{
    /// <summary>
    /// The tools the assistant may call. Not <c>get_card_details</c>, which returns card numbers.
    /// </summary>
    public static readonly IReadOnlySet<string> ReadOnly = new HashSet<string>(StringComparer.Ordinal)
    {
        "list_bank_accounts", "get_bank_account", "get_account_balances", "list_account_transactions",
        "get_ach_transfer", "get_wire_transfer", "get_usdc_transaction", "list_scheduled_ach_transfers", "get_approval",
        "list_contacts", "get_contact", "list_cards", "get_card", "list_card_transactions", "get_card_insights", "validate_routing_number"
    };

    readonly SemaphoreSlim _gate = new(1, 1);
    McpClient? _client;
    IReadOnlyList<AssistantTool>? _tools;

    BankingOptions Banking => options.Value.Banking;

    public bool IsAvailable => Banking.Provider == "meow" && Banking.Meow.ApiKey.Length > 0 && Banking.Meow.McpUrl.Length > 0;

    public async Task<IReadOnlyList<AssistantTool>> ToolsAsync(CancellationToken ct)
    {
        if (_tools is not null)
            return _tools;
        var client = await ClientAsync(ct);
        var listed = await client.ListToolsAsync(cancellationToken: ct);
        _tools = [.. listed.Where(t => ReadOnly.Contains(t.Name)).Select(t => new AssistantTool(
            t.Name,
            t.Description ?? t.Name,
            JsonNode.Parse(t.JsonSchema.GetRawText())!.AsObject()))];
        return _tools;
    }

    /// <summary>Runs a read-only tool. Anything not on the list is refused here too, whatever the model asked for.</summary>
    public async Task<(string Text, bool IsError)> CallAsync(string name, JsonObject input, CancellationToken ct)
    {
        if (!ReadOnly.Contains(name))
            return ($"The tool {name} is not available to the assistant.", true);
        var client = await ClientAsync(ct);
        var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(input.ToJsonString()) ?? [];
        var result = await client.CallToolAsync(name, arguments, cancellationToken: ct);
        var text = result.StructuredContent is { } structured
            ? structured.GetRawText()
            : string.Join("\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));
        return (text.Length > 60_000 ? text[..60_000] + "\n[truncated]" : text, result.IsError == true);
    }

    async Task<McpClient> ClientAsync(CancellationToken ct)
    {
        if (_client is not null)
            return _client;
        await _gate.WaitAsync(ct);
        try
        {
            _client ??= await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
            {
                Endpoint = new Uri(Banking.Meow.McpUrl),
                Name = "meow",
                // Meow's API-key surface takes the key as a bearer token, scoped to Ndeipi's business.
                AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + Banking.Meow.ApiKey }
            }, logs), loggerFactory: logs, cancellationToken: ct);
            return _client;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
    }
}
