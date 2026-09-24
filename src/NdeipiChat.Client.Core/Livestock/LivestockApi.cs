using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using NdeipiChat.Client.Realtime;
using NdeipiChat.Contracts;

namespace NdeipiChat.Client.Livestock;

/// <param name="Response">The server's answer; null if it didn't send one (e.g. a proxy error page).</param>
public sealed record RegistrationResult(int StatusCode, LivestockRegistrationResponse? Response)
{
    public bool IsSuccess => StatusCode == 200 && Response?.Status == RegistrationStatuses.Success;

    /// <summary>Worth sending again later: the server or the analysis service was unavailable.</summary>
    public bool IsRetryable => StatusCode is 408 or 429 || StatusCode >= 500;

    public string ProblemText => Response?.Problems is { Count: > 0 } problems
        ? string.Join(" ", problems)
        : $"The server couldn't take the registration ({StatusCode}).";
}

/// <summary>The livestock registry's HTTP endpoints.</summary>
public sealed class LivestockApi(HttpClient http)
{
    public async Task<OperatorKeyDto> RegisterOperatorKeyAsync(OperatorKeyRequest request, CancellationToken ct = default)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Post, Path(LivestockContract.OperatorKeysPath))
        {
            Content = JsonContent.Create(request, options: ContractJson.Options)
        }, ct);
        if (!response.IsSuccessStatusCode)
            throw await ApiException.FromResponseAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<OperatorKeyDto>(ContractJson.Options, ct))!;
    }

    /// <summary>
    /// Sends one registration. Refusals (bad photos, duplicates) come back as a result, not an
    /// exception -- only "couldn't reach the server" throws.
    /// </summary>
    public async Task<RegistrationResult> RegisterAsync(byte[] face, byte[] flank, string metadataJson, Guid keyId, string signature, CancellationToken ct = default)
    {
        var content = new MultipartFormDataContent
        {
            { Photo(face), LivestockContract.FaceImageField, "face" + Extension(face) },
            { Photo(flank), LivestockContract.FlankImageField, "flank" + Extension(flank) },
            { new StringContent(metadataJson, Encoding.UTF8, "application/json"), LivestockContract.MetadataField }
        };
        var request = new HttpRequestMessage(HttpMethod.Post, Path(LivestockContract.RegisterPath)) { Content = content };
        request.Headers.Add(LivestockContract.KeyIdHeader, keyId.ToString());
        request.Headers.Add(LivestockContract.SignatureHeader, signature);

        using var response = await SendAsync(request, ct);
        LivestockRegistrationResponse? body = null;
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                body = await response.Content.ReadFromJsonAsync<LivestockRegistrationResponse>(ContractJson.Options, ct);
            }
            catch (JsonException)
            {
            }
        }
        return new RegistrationResult((int)response.StatusCode, body);
    }

    public Task<List<CowSummaryDto>> GetCowsAsync(CancellationToken ct = default) =>
        GetJsonAsync<List<CowSummaryDto>>(Path(LivestockContract.CowsPath), ct);

    public Task<CowDetailDto> GetCowAsync(string cowId, CancellationToken ct = default) =>
        GetJsonAsync<CowDetailDto>($"{Path(LivestockContract.CowsPath)}/{Uri.EscapeDataString(cowId)}", ct);

    /// <summary>A photo of one of your animals, optionally downscaled to <paramref name="width"/> on its long edge.</summary>
    public async Task<byte[]> GetImageAsync(string reference, int? width = null, CancellationToken ct = default)
    {
        var path = $"{Path(LivestockContract.ImagesPath)}/{Uri.EscapeDataString(reference)}" + (width is { } w ? $"?width={w}" : "");
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (!response.IsSuccessStatusCode)
            throw await ApiException.FromResponseAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    async Task<T> GetJsonAsync<T>(string path, CancellationToken ct)
    {
        using var response = await SendAsync(new HttpRequestMessage(HttpMethod.Get, path), ct);
        if (!response.IsSuccessStatusCode)
            throw await ApiException.FromResponseAsync(response, ct);
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options, ct))!;
    }

    async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using (request)
        {
            try
            {
                return await http.SendAsync(request, ct);
            }
            catch (HttpRequestException ex)
            {
                throw new ApiException(null, "Can't reach the server. Check your connection.", ex);
            }
        }
    }

    static ByteArrayContent Photo(byte[] data)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(Extension(data) == ".png" ? "image/png" : "image/jpeg");
        return content;
    }

    static string Extension(byte[] data) => data is [0x89, 0x50, 0x4E, 0x47, ..] ? ".png" : ".jpg";

    static string Path(string contractPath) => contractPath.TrimStart('/');
}
