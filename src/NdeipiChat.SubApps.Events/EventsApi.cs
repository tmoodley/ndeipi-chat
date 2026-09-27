using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;
using QRCoder;

namespace NdeipiChat.SubApps.Events;

/// <summary>A message for the person using the app: a refusal from the server, or no connection.</summary>
public sealed class EventsException(string message, bool offline = false) : Exception(message)
{
    public bool Offline { get; } = offline;
}

/// <summary>The Events API, through the shell's signed-in HttpClient.</summary>
public sealed class EventsApi(HttpClient http)
{
    public const string Base = EventsContract.BasePath;

    public async Task<T> GetAsync<T>(string path) => (await SendAsync<T>(HttpMethod.Get, path, null))!;

    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body)
    {
        using var response = await SendCoreAsync(method, path, body);
        return response.StatusCode == HttpStatusCode.NoContent ? default : await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
    }

    public async Task SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var _ = await SendCoreAsync(method, path, body);
    }

    async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body)
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
            throw new EventsException("Can't reach the server. Check your connection.", offline: true);
        }
        if (response.IsSuccessStatusCode)
            return response;

        using (response)
        {
            throw new EventsException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.NotFound => "That isn't here any more.",
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
public abstract record EventsView
{
    public sealed record Discover : EventsView;
    public sealed record Event(Guid Id) : EventsView;
    public sealed record Tickets : EventsView;
    public sealed record Ticket(Guid Id) : EventsView;
    public sealed record Organize : EventsView;
    public sealed record Edit(Guid? Id) : EventsView;
    public sealed record Gate(Guid Id) : EventsView;
}

public static class Format
{
    public static string Price(string? price, string currency) =>
        price is null ? "" : decimal.TryParse(price, NumberStyles.Number, CultureInfo.InvariantCulture, out var p) && p == 0
            ? "Free"
            : $"{price} {currency.ToUpperInvariant()}";

    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("ddd d MMM · HH:mm", CultureInfo.CurrentCulture);

    public static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("dd", CultureInfo.CurrentCulture);

    public static string Month(DateTimeOffset at) => at.ToLocalTime().ToString("MMM", CultureInfo.CurrentCulture).ToUpperInvariant();

    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);

    /// <summary>A QR code as a PNG data URL, drawn in .NET so it works offline and in any shell.</summary>
    public static string QrDataUrl(string text)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        return "data:image/png;base64," + Convert.ToBase64String(new PngByteQRCode(data).GetGraphic(8));
    }
}
