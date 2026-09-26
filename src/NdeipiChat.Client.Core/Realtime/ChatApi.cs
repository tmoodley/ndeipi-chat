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

    public Task<ShamwariListDto> GetShamwarisAsync(CancellationToken ct = default) => GetAsync<ShamwariListDto>("api/shamwaris", ct);

    public Task<AddShamwariResponse> AddShamwariAsync(string contact, CancellationToken ct = default) =>
        SendAsync<AddShamwariResponse>(HttpMethod.Post, "api/shamwaris", new AddShamwariRequest(contact), ct);

    public Task<ShamwariListDto> AcceptShamwariAsync(Guid requestId, CancellationToken ct = default) =>
        SendAsync<ShamwariListDto>(HttpMethod.Post, $"api/shamwaris/requests/{requestId}/accept", null, ct);

    /// <summary>Declines a request to me, or cancels one I sent.</summary>
    public Task<ShamwariListDto> DeleteShamwariRequestAsync(Guid requestId, CancellationToken ct = default) =>
        SendAsync<ShamwariListDto>(HttpMethod.Delete, $"api/shamwaris/requests/{requestId}", null, ct);

    public Task<ShamwariListDto> RemoveShamwariAsync(Guid userId, CancellationToken ct = default) =>
        SendAsync<ShamwariListDto>(HttpMethod.Delete, $"api/shamwaris/{userId}", null, ct);

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

    public Task<FeedPageDto> GetFeedAsync(Guid? before = null, Guid? author = null, CancellationToken ct = default) =>
        GetAsync<FeedPageDto>("api/posts" + Query(("before", before), ("author", author)), ct);

    public Task<PostDto> GetPostAsync(Guid id, CancellationToken ct = default) => GetAsync<PostDto>($"api/posts/{id}", ct);

    /// <summary>Photos as JPEG or PNG bytes; the server re-encodes them.</summary>
    public Task<PostDto> CreatePostAsync(string? caption, IReadOnlyList<byte[]> photos, bool mint, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        if (!string.IsNullOrWhiteSpace(caption))
            form.Add(new StringContent(caption), "caption");
        form.Add(new StringContent(mint ? "true" : "false"), "mint");
        for (var i = 0; i < photos.Count; i++)
        {
            var part = new ByteArrayContent(photos[i]);
            part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(photos[i] is [0x89, 0x50, ..] ? "image/png" : "image/jpeg");
            form.Add(part, "photos", $"photo{i + 1}{(photos[i] is [0x89, 0x50, ..] ? ".png" : ".jpg")}");
        }
        return SendContentAsync<PostDto>(HttpMethod.Post, "api/posts", form, ct);
    }

    public Task<LikeResultDto> SetPostLikeAsync(Guid id, bool like, CancellationToken ct = default) =>
        SendAsync<LikeResultDto>(like ? HttpMethod.Post : HttpMethod.Delete, $"api/posts/{id}/like", null, ct);

    public Task<PostDto> MintPostAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<PostDto>(HttpMethod.Post, $"api/posts/{id}/mint", null, ct);

    public Task DeletePostAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/posts/{id}", null, ct);

    static string Query(params (string Name, Guid? Value)[] parameters)
    {
        var set = parameters.Where(p => p.Value is not null).Select(p => $"{p.Name}={p.Value}").ToList();
        return set.Count == 0 ? "" : "?" + string.Join('&', set);
    }

    Task<T> GetAsync<T>(string path, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, path, null, ct);

    Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct) =>
        SendContentAsync<T>(method, path, body is null ? null : JsonContent.Create(body, body.GetType(), options: ContractJson.Options), ct);

    async Task<T> SendContentAsync<T>(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = content };

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
