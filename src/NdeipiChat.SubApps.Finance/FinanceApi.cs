using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Finance;

/// <summary>A message for the person using the app: a refusal from the server, or no connection.</summary>
public sealed class FinanceException(string message) : Exception(message);

/// <summary>The Finance API, through the shell's signed-in HttpClient.</summary>
public sealed class FinanceApi(HttpClient http)
{
    public const string Base = FinanceContract.BasePath;

    public async Task<T> GetAsync<T>(string path) => (await SendAsync<T>(HttpMethod.Get, path, null))!;

    public Task<T?> SendAsync<T>(HttpMethod method, string path, object? body) =>
        SendAsync<T>(method, path, body is null ? null : JsonContent.Create(body, body.GetType(), options: ContractJson.Options));

    /// <summary>Uploads a document (a photo or PDF) for an application.</summary>
    public async Task<LoanApplicationDto> UploadAsync(Guid applicationId, string kind, string fileName, string contentType, byte[] bytes)
    {
        var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(bytes);
        part.Headers.ContentType = MediaTypeHeaderValue.TryParse(contentType, out var type) ? type : new MediaTypeHeaderValue("application/octet-stream");
        form.Add(part, "file", fileName);
        return (await SendAsync<LoanApplicationDto>(HttpMethod.Post, $"{Base}/applications/{applicationId}/documents/{kind}", form))!;
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
            throw new FinanceException("Can't reach the server. Check your connection.");
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return default;
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
            throw new FinanceException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.NotFound => "That isn't here any more.",
                HttpStatusCode.RequestEntityTooLarge => "That file is too big. Try a smaller photo or PDF.",
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
public abstract record FinanceView
{
    public sealed record Home : FinanceView;

    /// <summary>The application form: a new one (null), or a draft or returned one, at a step (1-5).</summary>
    public sealed record Apply(Guid? Id, int Step = 1) : FinanceView;

    public sealed record Tracker(Guid Id) : FinanceView;
    public sealed record Reviews : FinanceView;
    public sealed record Admin : FinanceView;
}

public static class Format
{
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static string Money(decimal amount) => $"{FinanceContract.Currency} {amount.ToString("N2", Invariant)}";

    public static string Money(decimal? amount) => amount is { } a ? Money(a) : "price to be confirmed";

    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string Size(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024:0.#} MB" : $"{Math.Max(1, bytes / 1024)} KB";

    public static string Label(IReadOnlyList<FinanceOption> options, string? code) => options.FirstOrDefault(o => o.Code == code)?.Label ?? code ?? "";

    public static string Status(string status, string? stage) => status switch
    {
        LoanStatuses.Draft => "Draft",
        LoanStatuses.InReview => stage is null ? "In review" : $"With the {ShortStage(stage)}",
        LoanStatuses.Returned => "Sent back for changes",
        LoanStatuses.Declined => "Declined",
        LoanStatuses.Approved => "Approved: ready to collect",
        LoanStatuses.Collected => "Collected",
        _ => status
    };

    public static string ShortStage(string stage) => stage switch
    {
        LoanStages.Village => "Village Productivity Committee",
        LoanStages.Ward => "Ward Development Committee",
        LoanStages.Chief => "Chief",
        LoanStages.Constituency => "Constituency CDF Committee",
        LoanStages.Province => "Provincial CDF Committee",
        LoanStages.Absa => "Absa",
        _ => stage
    };

    public static string Tone(string status) => status switch
    {
        LoanStatuses.Approved or LoanStatuses.Collected => "ok",
        LoanStatuses.Declined => "bad",
        LoanStatuses.Returned => "wait",
        _ => ""
    };
}
