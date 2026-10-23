using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;
using QRCoder;

namespace NdeipiChat.SubApps.Travel;

/// <summary>A message for the person using the app: the server's refusal, or no connection.</summary>
public sealed class TravelException(string message, bool offline = false) : Exception(message)
{
    public bool Offline { get; } = offline;
}

/// <summary>Lets Travel's API, through the shell's signed-in HttpClient.</summary>
public sealed class TravelApi(HttpClient http)
{
    public const string Base = TravelContract.BasePath;

    public async Task<T> GetAsync<T>(string path) => (await SendAsync<T>(HttpMethod.Get, path, null))!;

    public Task<T?> SendAsync<T>(HttpMethod method, string path, object? body) =>
        SendAsync<T>(method, path, body is null ? null : JsonContent.Create(body, body.GetType(), options: ContractJson.Options));

    public Task SendAsync(HttpMethod method, string path, object? body) => SendAsync<JsonElement?>(method, path, body);

    public async Task<T> UploadAsync<T>(string path, Stream file, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", string.IsNullOrWhiteSpace(fileName) ? "photo" : fileName);
        return (await SendAsync<T>(HttpMethod.Post, path, form))!;
    }

    public async Task<byte[]> DownloadAsync(string path)
    {
        using var response = await RawAsync(new HttpRequestMessage(HttpMethod.Get, path));
        return await response.Content.ReadAsByteArrayAsync();
    }

    async Task<T?> SendAsync<T>(HttpMethod method, string path, HttpContent? content)
    {
        using var response = await RawAsync(new HttpRequestMessage(method, path) { Content = content });
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0)
            return default;
        return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
    }

    async Task<HttpResponseMessage> RawAsync(HttpRequestMessage request)
    {
        HttpResponseMessage response;
        try
        {
            using (request)
                response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new TravelException("Can't reach the server. Check your connection.", offline: true);
        }
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw new TravelException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.NotFound => "That isn't here any more.",
                HttpStatusCode.RequestEntityTooLarge => "That photo is too big.",
                // The server's own failure: its problem title ("An error occurred…") says nothing useful.
                >= HttpStatusCode.InternalServerError => "Lets Travel isn't available right now. Please try again in a few minutes.",
                _ => await ProblemTitleAsync(response)
            });
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
public abstract record TravelView
{
    public sealed record Explore : TravelView;
    public sealed record Property(Guid Id, DateOnly? CheckIn, DateOnly? CheckOut, int Guests, int Rooms) : TravelView;
    public sealed record Tour(Guid Id, int Guests) : TravelView;
    public sealed record Checkout(Guid HoldId) : TravelView;
    public sealed record Trips : TravelView;
    public sealed record Booking(Guid Id) : TravelView;
    public sealed record Voucher(Guid Id) : TravelView;

    /// <summary>The vendor portal, at a tab: "listings", "bookings", "check-in" or "profile".</summary>
    public sealed record Vendor(string Tab = "listings") : TravelView;

    public sealed record EditProperty(Guid? Id) : TravelView;
    public sealed record EditTour(Guid? Id) : TravelView;
}

public static class Format
{
    public static string Money(decimal amount) => TravelContract.Money(amount);

    public static string Approx(string currency, decimal amount) => currency switch
    {
        "ZAR" => "R " + amount.ToString("#,0", CultureInfo.InvariantCulture),
        "ZMW" => "K " + amount.ToString("#,0", CultureInfo.InvariantCulture),
        "BWP" => "P " + amount.ToString("#,0", CultureInfo.InvariantCulture),
        _ => amount.ToString("#,0", CultureInfo.InvariantCulture) + " " + currency
    };

    public static string Day(DateOnly d) => d.ToString("ddd d MMM", CultureInfo.CurrentCulture);

    public static string DayYear(DateOnly d) => d.ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("ddd d MMM, HH:mm", CultureInfo.CurrentCulture);

    public static string Dates(TravelBookingItemDto i) =>
        i.CheckIn is { } a ? $"{Day(a)} → {Day(i.CheckOut!.Value)} · {Nights(a, i.CheckOut!.Value)}" : i.StartsAt is { } s ? When(s) : "";

    public static string Nights(DateOnly a, DateOnly b) => (b.DayNumber - a.DayNumber) is var n && n == 1 ? "1 night" : $"{n} nights";

    public static string Status(string status) => status switch
    {
        TravelBookingStatuses.PendingPayment => "Paying…",
        TravelBookingStatuses.Confirmed => "Confirmed",
        TravelBookingStatuses.CheckedIn => "Checked in",
        TravelBookingStatuses.Failed => "Payment failed",
        TravelBookingStatuses.Canceled => "Cancelled",
        _ => status
    };

    public static string Tone(string status) => status switch
    {
        TravelBookingStatuses.Confirmed or TravelBookingStatuses.CheckedIn or "published" or DepartureStatuses.Scheduled => "ok",
        TravelBookingStatuses.PendingPayment or "draft" or DepartureStatuses.Rescheduled => "wait",
        _ => "bad"
    };

    public static string Icon(string kind) => kind switch
    {
        "hotel" => "🏨", "lodge" or "eco_lodge" => "🛖", "bush_camp" or "campsite" => "⛺", "guesthouse" => "🏡", "chalet" => "🏔️", "apartment" => "🏢",
        "game_drive" or "multi_day_safari" => "🦁", "walking_safari" => "🥾", "river_cruise" => "🛶", "city_tour" => "🏙️", "cultural" => "🪘",
        "adventure" => "🪂", "scenic_flight" => "🚁", _ => "🧭"
    };

    /// <summary>A card's background: the photo, or a sunset gradient when there is none.</summary>
    public static string Cover(string? url) => url is null
        ? "background: linear-gradient(135deg, #F28C38, #C2410C 55%, #7C2D12)"
        : $"background: url('{url}') center / cover no-repeat";

    public static string Label(IReadOnlyList<(string Code, string Label)> list, string code) => TravelContract.Label(list, code);

    /// <summary>A QR code as a PNG data URL, drawn in .NET so it works offline.</summary>
    public static string QrDataUrl(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));
    }
}
