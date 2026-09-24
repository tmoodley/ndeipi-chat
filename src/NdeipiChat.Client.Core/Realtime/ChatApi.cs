using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Realtime;

/// <summary>The API's HTTP endpoints. Errors come back as <see cref="ApiException"/> with a message fit to show.</summary>
public sealed class ChatApi(HttpClient http)
{
    public const string HttpClientName = "ndeipi-api";

    public Task<MeDto> GetMeAsync(CancellationToken ct = default) => GetAsync<MeDto>("api/me", ct);

    public Task<UserWalletDto> SaveWalletAsync(UserWalletDto wallet, CancellationToken ct = default) =>
        SendAsync<UserWalletDto>(HttpMethod.Put, "api/me/wallets", wallet, ct);

    public Task RemoveWalletAsync(string chain, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/me/wallets/{Uri.EscapeDataString(chain)}", null, ct);

    public Task<List<UserDto>> SearchUsersAsync(string query, CancellationToken ct = default) =>
        GetAsync<List<UserDto>>($"api/users/search?q={Uri.EscapeDataString(query)}", ct);

    public Task<List<ConversationDto>> GetConversationsAsync(CancellationToken ct = default) =>
        GetAsync<List<ConversationDto>>("api/conversations", ct);

    public Task<ConversationDto> GetConversationAsync(Guid id, CancellationToken ct = default) =>
        GetAsync<ConversationDto>($"api/conversations/{id}", ct);

    public Task<ConversationDto> CreateConversationAsync(CreateConversationRequest request, CancellationToken ct = default) =>
        SendAsync<ConversationDto>(HttpMethod.Post, "api/conversations", request, ct);

    public Task<List<MessageDto>> GetMessagesAsync(Guid conversationId, Guid? before, int take, CancellationToken ct = default) =>
        GetAsync<List<MessageDto>>($"api/conversations/{conversationId}/messages?take={take}" + (before is { } b ? $"&before={b}" : ""), ct);

    public Task<MessageDto> SendMessageAsync(SendMessageRequest request, CancellationToken ct = default) =>
        SendAsync<MessageDto>(HttpMethod.Post, $"api/conversations/{request.ConversationId}/messages", request, ct);

    public Task<List<TokenRef>> GetTokensAsync(CancellationToken ct = default) => GetAsync<List<TokenRef>>("api/tokens", ct);

    public Task<BankingStatusDto> GetBankingStatusAsync(CancellationToken ct = default) => GetAsync<BankingStatusDto>("api/banking/status", ct);

    public Task<KycLinkDto> StartKycAsync(CancellationToken ct = default) => SendAsync<KycLinkDto>(HttpMethod.Post, "api/banking/kyc", null, ct);

    public Task<BankingStatusDto> RefreshBankingAsync(CancellationToken ct = default) =>
        SendAsync<BankingStatusDto>(HttpMethod.Post, "api/banking/refresh", null, ct);

    public Task<List<BalanceDto>> GetBalancesAsync(CancellationToken ct = default) => GetAsync<List<BalanceDto>>("api/banking/balances", ct);

    Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new ApiException(null, "Can't reach the server. Check your connection.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                throw await ApiException.FromResponseAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.NoContent || typeof(T) == typeof(object))
                return default!;
            return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct))!;
        }
    }
}

public sealed class ApiException(HttpStatusCode? statusCode, string message, Exception? inner = null) : Exception(message, inner)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;

    public static async Task<ApiException> FromResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string? title = null;
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (problem.RootElement.ValueKind == JsonValueKind.Object && problem.RootElement.TryGetProperty("title", out var t))
                title = t.GetString();
        }
        catch (JsonException)
        {
        }

        return new ApiException(response.StatusCode, response.StatusCode switch
        {
            HttpStatusCode.BadRequest when title is not null => title,
            HttpStatusCode.Unauthorized => "Your sign-in has expired. Please sign in again.",
            HttpStatusCode.NotFound => "That doesn't exist any more.",
            _ => title ?? "Something went wrong. Please try again."
        });
    }
}
