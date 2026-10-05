using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Market;

/// <summary>A message for the person using the app: a refusal from the server, or no connection.</summary>
public sealed class MarketException(string message, bool offline = false) : Exception(message)
{
    /// <summary>The server couldn't be reached (rather than refusing).</summary>
    public bool Offline { get; } = offline;
}

/// <summary>The Market API and the chat calls it needs, through the shell's signed-in HttpClient.</summary>
public sealed class MarketApi(HttpClient http)
{
    public const string Base = MarketContract.BasePath;

    public async Task<T> GetAsync<T>(string path) => (await SendAsync<T>(HttpMethod.Get, path, null))!;

    public Task<T?> SendAsync<T>(HttpMethod method, string path, object? body) =>
        SendAsync<T>(method, path, body is null ? null : JsonContent.Create(body, body.GetType(), options: ContractJson.Options));

    /// <summary>Posts a message (a listing or an offer) in a chat.</summary>
    public async Task<MessageDto> PostMessageAsync(Guid conversationId, string kind, object payload) =>
        (await SendAsync<MessageDto>(HttpMethod.Post, $"api/conversations/{conversationId}/messages",
            new SendMessageRequest(conversationId, kind, ContractJson.ToElement(payload), Guid.NewGuid())))!;

    /// <summary>A photo or video for a listing, uploaded to the chat it'll be posted in.</summary>
    public async Task<MediaItem> UploadAsync(Guid conversationId, Stream file, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        return (await SendAsync<MediaItem>(HttpMethod.Post, $"api/conversations/{conversationId}/media", form))!;
    }

    async Task<T?> SendAsync<T>(HttpMethod method, string path, HttpContent? content)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new MarketException("Can't reach the server. Check your connection.", offline: true);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return default;
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
            throw new MarketException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.NotFound => "That isn't here any more.",
                HttpStatusCode.RequestEntityTooLarge => "That file is too big. Try a shorter video or a smaller photo.",
                _ => await ProblemTitleAsync(response)
            });
        }
    }

    static async Task<string> ProblemTitleAsync(HttpResponseMessage response)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
            if (problem.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } text)
                return text;
        }
        catch (JsonException)
        {
        }
        return "Something went wrong. Please try again.";
    }
}

/// <summary>Where the app is. The root keeps one and renders the matching page.</summary>
public abstract record MarketView
{
    public sealed record Feed(string? Tag = null) : MarketView;

    /// <summary>A listing; <see cref="OfferIn"/> opens the offer form for that chat.</summary>
    public sealed record Listing(Guid Id, Guid? OfferIn = null) : MarketView;

    /// <summary>The sell form, posting into <see cref="ChatId"/> if given.</summary>
    public sealed record Sell(Guid? ChatId = null) : MarketView;

    public sealed record Mine : MarketView;
}

public static class Format
{
    public static string Ago(DateTimeOffset at)
    {
        var gone = DateTimeOffset.UtcNow - at;
        return gone.TotalMinutes < 1 ? "just now"
            : gone.TotalHours < 1 ? $"{(int)gone.TotalMinutes} min ago"
            : gone.TotalDays < 1 ? $"{(int)gone.TotalHours} h ago"
            : gone.TotalDays < 7 ? $"{(int)gone.TotalDays} d ago"
            : at.ToLocalTime().ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture);
    }

    public static string Tone(string status) => status switch
    {
        ListingStatuses.Available or OfferStatuses.Accepted => "ok",
        ListingStatuses.Upcoming or OfferStatuses.Pending => "wait",
        _ => "bad"
    };

    /// <summary>"3 · Boer goats · 5 years old · Monze".</summary>
    public static string Details(ListingDto l) => string.Join(" · ", new[]
    {
        l.Quantity > 1 ? l.Quantity.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) : null,
        string.Join(' ', new[] { l.Breed, MarketContract.SpeciesLabel(l.Species).ToLowerInvariant() }.Where(s => !string.IsNullOrEmpty(s))),
        l.Age,
        l.Location is { } place ? "📍 " + place : null
    }.Where(s => !string.IsNullOrEmpty(s)));

    public static string? Cover(ListingDto l) => l.Media.FirstOrDefault(m => m.Type == MediaTypes.Image)?.ThumbUrl;

    public static string OfferAmount(OfferDto o) => o.Kind == OfferKinds.Barter ? "🔁 Trade" : MarketContract.Price(o.Amount, o.Currency ?? MarketContract.DefaultCurrency);
}
