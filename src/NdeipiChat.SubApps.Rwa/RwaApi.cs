using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Rwa;

/// <summary>A message for the person using the app: the server's refusal, or no connection.</summary>
public sealed class RwaException(string message) : Exception(message);

/// <summary>Ndeipi RWA's API, through the shell's signed-in HttpClient.</summary>
public sealed class RwaApi(HttpClient http)
{
    const string Base = RwaContract.BasePath;

    public Task<RwaMeDto> MeAsync() => GetAsync<RwaMeDto>("me");

    public Task<List<RwaAssetDto>> AssetsAsync() => GetAsync<List<RwaAssetDto>>("assets");

    public Task<RwaAssetDetailDto> AssetAsync(string code) => GetAsync<RwaAssetDetailDto>($"assets/{Uri.EscapeDataString(code)}");

    public Task<RwaPortfolioDto> PortfolioAsync() => GetAsync<RwaPortfolioDto>("portfolio");

    public Task<RwaHoldingDto> MintAsync(RwaMintRequest request) => PostAsync<RwaHoldingDto>("presale/mint", request);

    public Task<RwaHoldingDto> CancelAsync(Guid id) => PostAsync<RwaHoldingDto>($"holdings/{id}/cancel", new { });

    public Task<RwaAssetDto> PostNavAsync(RwaNavUpdateRequest request) => PostAsync<RwaAssetDto>("admin/nav", request);

    async Task<T> GetAsync<T>(string path) => await ReadAsync<T>(await SendAsync(new HttpRequestMessage(HttpMethod.Get, $"{Base}/{path}")));

    async Task<T> PostAsync<T>(string path, object body) =>
        await ReadAsync<T>(await SendAsync(new HttpRequestMessage(HttpMethod.Post, $"{Base}/{path}")
        {
            Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options)
        }));

    static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        using (response)
            return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request)
    {
        HttpResponseMessage response;
        try
        {
            using (request)
                response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new RwaException("Can't reach the server. Check your connection.");
        }
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw new RwaException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.Forbidden => "You can't do that here.",
                HttpStatusCode.NotFound => "That isn't here any more.",
                >= HttpStatusCode.InternalServerError => "Ndeipi RWA isn't available right now. Please try again in a few minutes.",
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
public abstract record RwaView
{
    public sealed record Catalogue : RwaView;
    public sealed record Villa(string Code) : RwaView;
    public sealed record Portfolio(Guid? Highlight = null) : RwaView;
}
