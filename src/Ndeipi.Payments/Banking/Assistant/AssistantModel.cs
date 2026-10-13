using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Beta.Messages;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Banking.Assistant;

/// <summary>A tool the assistant may call: a JSON Schema for its input, in the Messages API's shape.</summary>
public sealed record AssistantTool(string Name, string Description, JsonObject InputSchema);

/// <summary>One request: messages in the Messages API's wire format, exactly as stored.</summary>
public sealed record AssistantCall(string System, IReadOnlyList<AssistantTool> Tools, IReadOnlyList<JsonObject> Messages);

/// <summary>The reply's content blocks in wire format, why it stopped, and which model answered.</summary>
public sealed record AssistantResult(JsonArray Content, string StopReason, string Model);

/// <summary>The language model behind the assistant; tests replace it with a script.</summary>
public interface IAssistantModel
{
    bool IsConfigured { get; }

    Task<AssistantResult> CreateAsync(AssistantCall call, CancellationToken ct);
}

/// <summary>
/// Claude, through the Anthropic SDK. Claude Opus 5.5 with adaptive thinking (always on for this
/// model) at medium effort. Server-side fallback is on: if Claude declines on policy grounds, the
/// API retries on a substitute model inside the same call rather than failing.
///
/// History is replayed byte for byte from what was stored, thinking blocks included, because the
/// API checks that earlier turns were not edited.
/// </summary>
public sealed class ClaudeAssistantModel(IOptions<PaymentsOptions> options) : IAssistantModel
{
    BankingOptions.AssistantOptions Settings => options.Value.Banking.Assistant;

    public bool IsConfigured => Settings.ApiKey.Length > 0;

    AnthropicClient? _client;

    public async Task<AssistantResult> CreateAsync(AssistantCall call, CancellationToken ct)
    {
        _client ??= new AnthropicClient { ApiKey = Settings.ApiKey };
        var response = await _client.Beta.Messages.Create(new MessageCreateParams
        {
            Model = Settings.Model,
            MaxTokens = 16000,
            Betas = ["server-side-fallback-2026-07-01"],
            Fallbacks = new Default(),
            OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
            System = call.System,
            Tools = [.. call.Tools.Select(t => (BetaToolUnion)BetaTool.FromRawUnchecked(Raw(new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = t.InputSchema.DeepClone()
            })))],
            Messages = [.. call.Messages.Select(m => BetaMessageParam.FromRawUnchecked(Raw(m)))]
        }, ct);

        return new AssistantResult(
            JsonSerializer.SerializeToNode(response.Content)!.AsArray(),
            response.StopReason?.ToString() ?? "end_turn",
            response.Model.ToString());
    }

    static IReadOnlyDictionary<string, JsonElement> Raw(JsonObject value) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(value.ToJsonString())!;
}
