using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NdeipiChat.Api.Banking;

public sealed class BridgeOptions
{
    public const string Section = "Bridge";

    /// <summary>Sandbox by default; production is https://api.bridge.xyz/v0/.</summary>
    public string BaseUrl { get; set; } = "https://api.sandbox.bridge.xyz/v0/";

    public string ApiKey { get; set; } = "";

    /// <summary>PEM public key shown when the webhook endpoint is created in Bridge.</summary>
    public string? WebhookPublicKey { get; set; }

    public TimeSpan WebhookTolerance { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Chain each user's Bridge custodial wallet is created on.</summary>
    public string WalletChain { get; set; } = "solana";

    /// <summary>Stablecoin users send each other, e.g. "usdc" or "usdb".</summary>
    public string Currency { get; set; } = "usdc";

    public decimal MaxTransferAmount { get; set; } = 10_000m;

    /// <summary>Where Bridge's hosted KYC flow sends the user when finished (optional).</summary>
    public string? KycRedirectUri { get; set; }

    /// <summary>How often to check transfers Bridge hasn't reported on by webhook. Zero turns it off.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public bool IsConfigured => ApiKey.Length > 0;
}

/// <summary>Bridge API v0 -- only the endpoints the app uses.</summary>
public sealed class BridgeClient(HttpClient http)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public Task<BridgeKycLink> CreateKycLinkAsync(BridgeKycLinkRequest request, string idempotencyKey, CancellationToken ct) =>
        SendAsync<BridgeKycLink>(HttpMethod.Post, "kyc_links", request, idempotencyKey, ct);

    public Task<BridgeKycLink> GetKycLinkAsync(string kycLinkId, CancellationToken ct) =>
        SendAsync<BridgeKycLink>(HttpMethod.Get, $"kyc_links/{Uri.EscapeDataString(kycLinkId)}", null, null, ct);

    public Task<BridgeWallet> CreateWalletAsync(string customerId, string chain, string idempotencyKey, CancellationToken ct) =>
        SendAsync<BridgeWallet>(HttpMethod.Post, $"customers/{Uri.EscapeDataString(customerId)}/wallets", new { chain }, idempotencyKey, ct);

    public Task<BridgeWallet> GetWalletAsync(string customerId, string walletId, CancellationToken ct) =>
        SendAsync<BridgeWallet>(HttpMethod.Get, $"customers/{Uri.EscapeDataString(customerId)}/wallets/{Uri.EscapeDataString(walletId)}", null, null, ct);

    public Task<BridgeTransfer> CreateTransferAsync(BridgeTransferRequest request, string idempotencyKey, CancellationToken ct) =>
        SendAsync<BridgeTransfer>(HttpMethod.Post, "transfers", request, idempotencyKey, ct);

    public Task<BridgeTransfer> GetTransferAsync(string transferId, CancellationToken ct) =>
        SendAsync<BridgeTransfer>(HttpMethod.Get, $"transfers/{Uri.EscapeDataString(transferId)}", null, null, ct);

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        // Bridge requires one on every POST; reusing it makes a retry return the original result.
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new BridgeApiException(response.StatusCode, text);

        return JsonSerializer.Deserialize<T>(text, Json)
            ?? throw new BridgeApiException(response.StatusCode, "Empty response body.");
    }
}

public sealed class BridgeApiException(HttpStatusCode statusCode, string body)
    : Exception($"Bridge returned {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;
    public string Body { get; } = body;

    /// <summary>Bridge refused the request itself; retrying won't change the answer.</summary>
    public bool IsClientError => (int)StatusCode is >= 400 and < 500 && StatusCode is not HttpStatusCode.TooManyRequests;
}

public sealed record BridgeKycLinkRequest(string FullName, string Email, string Type, string? RedirectUri);

public sealed record BridgeKycLink(
    string? Id,
    string? KycLink,
    string? TosLink,
    string? KycStatus,
    string? TosStatus,
    string? CustomerId);

public sealed record BridgeWallet(string Id, string? Chain, string? Address, List<BridgeBalance>? Balances);

public sealed record BridgeBalance(string? Balance, string? Currency, string? Chain, string? ContractAddress);

public sealed record BridgeTransferRequest(
    string Amount,
    string OnBehalfOf,
    BridgeTransferEndpoint Source,
    BridgeTransferEndpoint Destination);

public sealed record BridgeTransferEndpoint(
    string PaymentRail,
    string Currency,
    string? BridgeWalletId = null,
    string? ToAddress = null);

public sealed record BridgeTransfer(string Id, string? State, string? Amount);

public sealed record BridgeWebhookEvent(
    string? EventId,
    string? EventCategory,
    string? EventType,
    string? EventObjectId,
    string? EventObjectStatus,
    JsonElement? EventObject);
