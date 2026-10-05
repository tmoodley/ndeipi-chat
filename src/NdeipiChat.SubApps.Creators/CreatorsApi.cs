using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Creators;

/// <summary>A message for the person using the app: a refusal from the server, or no connection.</summary>
public sealed class CreatorsException(string message) : Exception(message);

/// <summary>The Creators API, through the shell's signed-in HttpClient.</summary>
public sealed class CreatorsApi(HttpClient http)
{
    public const string Base = CreatorsContract.BasePath;

    public async Task<T> GetAsync<T>(string path) => (await SendAsync<T>(HttpMethod.Get, path, null))!;

    public Task<T?> SendAsync<T>(HttpMethod method, string path, object? body) =>
        SendAsync<T>(method, path, body is null ? null : JsonContent.Create(body, body.GetType(), options: ContractJson.Options));

    /// <summary>Uploads a photo, audio or video (to the studio's media, or as the banner).</summary>
    public async Task<T> UploadAsync<T>(string path, Stream file, string fileName, string contentType)
    {
        var form = new MultipartFormDataContent();
        var part = new StreamContent(file);
        part.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", string.IsNullOrWhiteSpace(fileName) ? "file" : fileName);
        return (await SendAsync<T>(HttpMethod.Post, path, form))!;
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
            throw new CreatorsException("Can't reach the server. Check your connection.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return default;
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
            throw new CreatorsException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.NotFound => "That isn't here any more.",
                HttpStatusCode.RequestEntityTooLarge => "That file is too big.",
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
public abstract record CreatorsView
{
    public sealed record Discover : CreatorsView;
    public sealed record Storefront(Guid CreatorId) : CreatorsView;
    public sealed record Post(Guid PostId) : CreatorsView;

    /// <summary>The creator's studio, at a tab: "overview", "posts", "tiers", "page", "audience" or "payouts".</summary>
    public sealed record Studio(string Tab = "overview") : CreatorsView;

    /// <summary>Writing or editing a post.</summary>
    public sealed record EditPost(Guid? PostId) : CreatorsView;
}

public static class Format
{
    public static string Money(decimal amount) => CreatorsContract.Money(amount);

    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture);

    public static string WhenTime(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy, HH:mm", System.Globalization.CultureInfo.CurrentCulture);

    public static string Period(string period) => period == BillingPeriods.Annual ? "year" : "month";

    public static string Status(CreatorSubscriptionDto s) => s.Status switch
    {
        SubscriptionStatuses.Active when s.LastPaymentStatus is TransferStatuses.Pending or TransferStatuses.Processing => "Payment on its way",
        SubscriptionStatuses.Active when s.CancelAtPeriodEnd => $"Ends {When(s.CurrentPeriodEnd)}",
        SubscriptionStatuses.Active => $"Renews {When(s.CurrentPeriodEnd)}",
        SubscriptionStatuses.Grace => $"Payment failed: top up by {When(s.GraceUntil ?? s.CurrentPeriodEnd)}",
        _ => "Ended"
    };

    public static string Tone(string status) => status switch
    {
        SubscriptionStatuses.Active or TransferStatuses.Confirmed or PayoutStatuses.Paid => "ok",
        SubscriptionStatuses.Grace or TransferStatuses.Pending or TransferStatuses.Processing or PayoutStatuses.Processing => "wait",
        _ => "bad"
    };

    public static string Access(CreatorPostDto p) => p.Access switch
    {
        PostAccess.Public => "Public",
        PostAccess.Tier => $"{p.TierName}+",
        _ => p.TierName is { } tier ? $"{Money(p.Price ?? 0)} or {tier}+" : Money(p.Price ?? 0)
    };
}
