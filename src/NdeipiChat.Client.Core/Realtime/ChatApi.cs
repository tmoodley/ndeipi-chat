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

    public Task<TrustScoreDto> GetTrustScoreAsync(Guid userId, CancellationToken ct = default) =>
        GetAsync<TrustScoreDto>($"{TrustContract.BasePath}/{userId}", ct);

    public Task<MyTrustDto> GetMyTrustAsync(CancellationToken ct = default) => GetAsync<MyTrustDto>($"{TrustContract.BasePath}/me", ct);

    public Task<MyTrustDto> RefreshTrustAsync(CancellationToken ct = default) =>
        SendAsync<MyTrustDto>(HttpMethod.Post, $"{TrustContract.BasePath}/refresh", null, ct);

    public Task<TrustSettingsDto> GetTrustSettingsAsync(CancellationToken ct = default) =>
        GetAsync<TrustSettingsDto>($"{TrustContract.BasePath}/settings", ct);

    public Task<TrustLinkStartDto> StartTrustLinkAsync(string platform, CancellationToken ct = default) =>
        SendAsync<TrustLinkStartDto>(HttpMethod.Post, $"{TrustContract.BasePath}/links/{Uri.EscapeDataString(platform)}", null, ct);

    public Task<MyTrustDto> LinkTelegramAsync(TelegramLoginRequest login, CancellationToken ct = default) =>
        SendAsync<MyTrustDto>(HttpMethod.Post, $"{TrustContract.BasePath}/links/telegram", login, ct);

    public Task<MyTrustDto> UnlinkTrustAsync(string platform, CancellationToken ct = default) =>
        SendAsync<MyTrustDto>(HttpMethod.Delete, $"{TrustContract.BasePath}/links/{Uri.EscapeDataString(platform)}", null, ct);

    public Task<LauncherManifestDto> GetLauncherAsync(CancellationToken ct = default) => GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath, ct);

    public Task<LauncherManifestDto> SetPinsAsync(IReadOnlyList<string> appIds, CancellationToken ct = default) =>
        SendAsync<LauncherManifestDto>(HttpMethod.Put, LauncherContract.PinsPath, new SetPinsRequest(appIds), ct);

    /// <param name="sort"><see cref="FeedSorts"/>; Top pages by <paramref name="skip"/> instead of <paramref name="before"/>.</param>
    /// <param name="imagesOnly">Only posts with photos.</param>
    public Task<FeedPageDto> GetFeedAsync(Guid? before = null, Guid? author = null, CancellationToken ct = default, string? scope = null, Guid? group = null,
        string? sort = null, int skip = 0, bool imagesOnly = false)
    {
        var query = new List<string>();
        foreach (var (name, value) in new[] { ("before", before), ("author", author), ("group", group) })
            if (value is { } v)
                query.Add($"{name}={v}");
        if (scope is not null)
            query.Add("scope=" + Uri.EscapeDataString(scope));
        if (sort is not null)
            query.Add("sort=" + Uri.EscapeDataString(sort));
        if (skip > 0)
            query.Add($"skip={skip}");
        if (imagesOnly)
            query.Add("images=true");
        return GetAsync<FeedPageDto>("api/posts" + (query.Count == 0 ? "" : "?" + string.Join('&', query)), ct);
    }

    public Task<List<CommentDto>> GetCommentsAsync(Guid postId, CancellationToken ct = default) =>
        GetAsync<List<CommentDto>>($"api/posts/{postId}/comments", ct);

    public Task<CommentDto> AddCommentAsync(Guid postId, string text, CancellationToken ct = default) =>
        SendAsync<CommentDto>(HttpMethod.Post, $"api/posts/{postId}/comments", new AddCommentRequest(text), ct);

    public Task DeleteCommentAsync(Guid postId, Guid commentId, CancellationToken ct = default) =>
        SendAsync<object>(HttpMethod.Delete, $"api/posts/{postId}/comments/{commentId}", null, ct);

    /// <summary>Someone's comments, with the posts they're on.</summary>
    public Task<List<CommentActivityDto>> GetCommentActivityAsync(Guid authorId, CancellationToken ct = default) =>
        GetAsync<List<CommentActivityDto>>($"api/posts/comments?author={authorId}", ct);

    /// <summary>Returns the repost.</summary>
    public Task<PostDto> RepostAsync(Guid postId, string? caption = null, CancellationToken ct = default) =>
        SendAsync<PostDto>(HttpMethod.Post, $"api/posts/{postId}/repost", new RepostRequest(caption), ct);

    /// <summary>Returns the original, with its counts updated.</summary>
    public Task<PostDto> UndoRepostAsync(Guid postId, CancellationToken ct = default) =>
        SendAsync<PostDto>(HttpMethod.Delete, $"api/posts/{postId}/repost", null, ct);

    public Task<PostDto> GetPostAsync(Guid id, CancellationToken ct = default) => GetAsync<PostDto>($"api/posts/{id}", ct);

    /// <summary>Photos as JPEG or PNG bytes; the server re-encodes them.</summary>
    public Task<PostDto> CreatePostAsync(string? caption, IReadOnlyList<byte[]> photos, bool mint, Guid? groupId = null, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        if (groupId is { } group)
            form.Add(new StringContent(group.ToString()), "groupId");
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

    /// <summary>For pages without a dedicated method here (Social's profiles and groups).</summary>
    public Task<T> GetJsonAsync<T>(string path, CancellationToken ct = default) => GetAsync<T>(path, ct);

    public Task<T> SendJsonAsync<T>(HttpMethod method, string path, object? body = null, CancellationToken ct = default) => SendAsync<T>(method, path, body, ct);

    /// <summary>Uploads one image as multipart/form-data field "image" (avatars, covers).</summary>
    public Task<T> UploadImageAsync<T>(string path, byte[] image, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(image);
        part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(image is [0x89, 0x50, ..] ? "image/png" : "image/jpeg");
        form.Add(part, "image", image is [0x89, 0x50, ..] ? "image.png" : "image.jpg");
        return SendContentAsync<T>(HttpMethod.Post, path, form, ct);
    }

    /// <summary>A photo or video for a chat; send it next in a "media" message (or a listing) by its id.</summary>
    public Task<MediaItem> UploadChatMediaAsync(Guid conversationId, Stream file, string fileName, string contentType, CancellationToken ct = default)
    {
        var form = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(contentType, out var type)
            ? type
            : new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", string.IsNullOrWhiteSpace(fileName) ? "media" : fileName);
        return SendContentAsync<MediaItem>(HttpMethod.Post, $"api/conversations/{conversationId}/media", form, ct);
    }

    public Task<List<MessageDto>> ForwardAsync(Guid messageId, IReadOnlyList<Guid> conversationIds, CancellationToken ct = default) =>
        SendAsync<List<MessageDto>>(HttpMethod.Post, $"api/messages/{messageId}/forward", new ForwardRequest(conversationIds), ct);

    /// <summary>The seller answers an offer, or the buyer withdraws it: "accept", "decline" or "withdraw".</summary>
    public Task<OfferDto> AnswerOfferAsync(Guid offerId, string answer, CancellationToken ct = default) =>
        SendAsync<OfferDto>(HttpMethod.Post, $"{MarketContract.BasePath}/offers/{offerId}/{answer}", null, ct);

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
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // HttpClient's own timeout, not the caller cancelling: a slow or dropped connection.
            throw new ApiException(null, "The server took too long to answer. Check your connection.", ex);
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
