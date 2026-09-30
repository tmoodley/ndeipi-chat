using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Donations;

/// <summary>A message for the person using the app: a refusal from the server, or no connection.</summary>
public sealed class DonationsException(string message) : Exception(message);

/// <summary>The Donations API, through the shell's signed-in HttpClient.</summary>
public sealed class DonationsApi(HttpClient http)
{
    public const string Base = DonationsContract.BasePath;

    public async Task<T> GetAsync<T>(string path) => (await SendAsync<T>(HttpMethod.Get, path, null))!;

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
            throw new DonationsException("Can't reach the server. Check your connection.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return default;
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
            throw new DonationsException(response.StatusCode switch
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
public abstract record DonationsView
{
    public sealed record Feed : DonationsView;
    public sealed record Campaign(Guid Id) : DonationsView;
    public sealed record Giving : DonationsView;
    public sealed record Receipt(Guid Id) : DonationsView;
    public sealed record Organize : DonationsView;
    public sealed record Edit(Guid? Id) : DonationsView;
}

public static class Format
{
    /// <summary>"$12.50" for the dollar stablecoins; "12.50 XYZ" otherwise.</summary>
    public static string Money(decimal amount, string currency) =>
        currency.ToLowerInvariant() is "usdc" or "usdb" or "usdt" or "usd"
            ? "$" + amount.ToString(amount == Math.Floor(amount) ? "N0" : "N2", CultureInfo.InvariantCulture)
            : $"{amount.ToString("N2", CultureInfo.InvariantCulture)} {currency.ToUpperInvariant()}";

    /// <summary>How far to the target, 0 to 100 (a campaign past its target shows full).</summary>
    public static int Percent(CampaignDto c) => c.Target <= 0 ? 0 : (int)Math.Min(100, Math.Floor(c.Raised / c.Target * 100));

    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    /// <summary>"just now", "5 min ago", "3 h ago", then the date.</summary>
    public static string Ago(DateTimeOffset at)
    {
        var gone = DateTimeOffset.UtcNow - at;
        return gone.TotalMinutes < 1 ? "just now"
            : gone.TotalHours < 1 ? $"{(int)gone.TotalMinutes} min ago"
            : gone.TotalDays < 1 ? $"{(int)gone.TotalHours} h ago"
            : at.ToLocalTime().ToString("d MMM", CultureInfo.CurrentCulture);
    }

    public static string? EndsIn(DateTimeOffset? ends)
    {
        if (ends is not { } e)
            return null;
        var left = e - DateTimeOffset.UtcNow;
        return left <= TimeSpan.Zero ? "Ended" : left.TotalDays >= 2 ? $"{(int)left.TotalDays} days left" : left.TotalHours >= 2 ? $"{(int)left.TotalHours} hours left" : "Ends soon";
    }
}
