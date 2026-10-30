using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Lending;

/// <summary>A message for the person using the app: the server's refusal, or no connection.</summary>
public sealed class LendingException(string message) : Exception(message);

/// <summary>Ndeipi Lending's API, through the shell's signed-in HttpClient.</summary>
public sealed class LendingApi(HttpClient http)
{
    const string Base = LendingContract.BasePath;

    public Task<LendingMeDto> MeAsync() => SendAsync<LendingMeDto>(HttpMethod.Get, "me");

    public Task<LendingDealDto> DealAsync(Guid id) => SendAsync<LendingDealDto>(HttpMethod.Get, $"deals/{id}");

    public Task<List<LendingAuditDto>> AuditAsync(Guid id) => SendAsync<List<LendingAuditDto>>(HttpMethod.Get, $"deals/{id}/audit");

    public Task<LendingDealDto> CreateAsync(LendingTermsRequest request) => SendAsync<LendingDealDto>(HttpMethod.Post, "deals", request);

    public Task<LendingDealDto> ReferenceAsync() => SendAsync<LendingDealDto>(HttpMethod.Post, "deals/reference", new { });

    /// <summary>Any change to a deal: every one answers with the deal as it now stands.</summary>
    public Task<LendingDealDto> ActAsync(Guid id, string action, object? body = null) => SendAsync<LendingDealDto>(HttpMethod.Post, $"deals/{id}/{action}", body ?? new { });

    public Task<BrokerPipelineDto> PipelineAsync() => SendAsync<BrokerPipelineDto>(HttpMethod.Get, "broker/pipeline");

    public Task<LenderPortfolioDto> PortfolioAsync() => SendAsync<LenderPortfolioDto>(HttpMethod.Get, "lender/portfolio");

    public Task<BorrowerLoansDto> LoansAsync() => SendAsync<BorrowerLoansDto>(HttpMethod.Get, "borrower/loans");

    public Task<List<LendingNoticeDto>> NoticesAsync() => SendAsync<List<LendingNoticeDto>>(HttpMethod.Get, "notices");

    public async Task MarkNoticesReadAsync() => (await RawAsync(HttpMethod.Post, "notices/read", new { })).Dispose();

    public async Task SetProfileAsync(LendingProfileRequest request) => (await RawAsync(HttpMethod.Put, "compliance/profiles", request)).Dispose();

    public async Task TopUpAsync(LendingTopUpRequest request) => (await RawAsync(HttpMethod.Post, "compliance/top-up", request)).Dispose();

    async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null)
    {
        using var response = await RawAsync(method, path, body);
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, object? body)
    {
        using var request = new HttpRequestMessage(method, $"{Base}/{path}");
        if (body is not null)
            request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new LendingException("Can't reach the server. Check your connection.");
        }
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw new LendingException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized => "Your sign-in has expired. Reload the page.",
                HttpStatusCode.Forbidden => "You don't have access to Ndeipi Lending.",
                HttpStatusCode.NotFound => "That isn't here any more.",
                >= HttpStatusCode.InternalServerError => "Ndeipi Lending isn't available right now. Please try again in a few minutes.",
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
