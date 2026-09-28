using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Gigs;

/// <summary>A message for the person using the app: a refusal from the server, or no connection.</summary>
public sealed class GigsException(string message) : Exception(message);

/// <summary>The Gigs API, and the chat API for a gig's thread, through the shell's signed-in HttpClient.</summary>
public sealed class GigsApi(HttpClient http)
{
    public const string Base = GigsContract.BasePath;

    public async Task<T?> GetAsync<T>(string path) => await SendAsync<T>(HttpMethod.Get, path, null);

    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new GigsException("Can't reach the server. Check your connection.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return default;
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
            throw new GigsException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.NotFound => "That isn't here any more.",
                _ => await ProblemTitleAsync(response)
            });
        }
    }

    /// <summary>A gig action such as "accept"; returns the gig as it now is.</summary>
    public async Task<GigDto> ActAsync(Guid gigId, string action, object? body = null) =>
        (await SendAsync<GigDto>(HttpMethod.Post, $"{Base}/{gigId}/{action}", body ?? new { }))!;

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
public abstract record GigsView
{
    public sealed record Board : GigsView;
    public sealed record Map : GigsView;
    public sealed record Mine : GigsView;
    public sealed record Profile : GigsView;
    public sealed record Gig(Guid Id) : GigsView;
    public sealed record NewGig(double Latitude, double Longitude, Guid? WorkerId = null, string? WorkerName = null, Guid? ConversationId = null) : GigsView;
}

public static class Format
{
    /// <summary>Where the map opens before we know better: Harare.</summary>
    public const double DefaultLatitude = -17.8292, DefaultLongitude = 31.0522;

    public static string Money(string amount, string symbol) => $"{amount} {symbol}";

    public static string Km(double? km) => km is null ? "" : km < 1 ? "under 1 km away" : $"{km:0.#} km away";

    public static string Stars(double? rating, int count) => rating is null ? "New" : $"★ {rating:0.0} ({count})";

    public static string Place(double latitude, double longitude) =>
        string.Create(CultureInfo.InvariantCulture, $"{latitude:0.0000}, {longitude:0.0000}");

    public static string Status(GigDto gig) => gig.Status switch
    {
        GigStatuses.Open when gig.MyOffer == GigOfferStatuses.Offered => "Offered to you",
        GigStatuses.Open => gig.IsClient ? "Looking for someone" : "Open",
        GigStatuses.Assigned => gig.IsClient ? $"{gig.Worker?.DisplayName} is on it" : "In progress",
        GigStatuses.Submitted => gig.IsClient ? "Done: approve and pay" : "Waiting for payment",
        GigStatuses.Completed => gig.PaymentStatus switch
        {
            TransferStatuses.Confirmed => "Paid",
            TransferStatuses.Failed => "Payment failed",
            null => "Done",
            _ => "Paying…"
        },
        _ => "Cancelled"
    };
}

public static class SkillLook
{
    public static string Emoji(string skill) => skill switch
    {
        "delivery" => "🛵",
        "photography" => "📸",
        "video" => "🎬",
        "design" => "🎨",
        "development" => "💻",
        "construction" => "🔨",
        "cleaning" => "🧽",
        "agriculture" => "🌾",
        "tutoring" => "📚",
        "events" => "🎪",
        _ => "🧰"
    };

    /// <summary>The shell's colour classes (tone-*), one per category.</summary>
    public static string Tone(string skill) => skill switch
    {
        "delivery" => "tone-orange",
        "photography" => "tone-pink",
        "video" => "tone-red",
        "design" => "tone-purple",
        "development" => "tone-blue",
        "construction" => "tone-brown",
        "cleaning" => "tone-teal",
        "agriculture" => "tone-green",
        "tutoring" => "tone-yellow",
        "events" => "tone-purple",
        _ => "tone-blue"
    };

    public static string Ago(DateTimeOffset at)
    {
        var age = DateTimeOffset.UtcNow - at;
        return age.TotalMinutes < 1 ? "just now"
            : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
            : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
            : $"{(int)age.TotalDays} d ago";
    }
}
