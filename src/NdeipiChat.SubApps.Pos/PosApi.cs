using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Pos;

/// <summary>A message for the person at the till. <see cref="Offline"/>: no connection. <see cref="Locked"/>: the till needs a PIN again.</summary>
public sealed class PosException(string message, bool offline = false, bool locked = false) : Exception(message)
{
    public bool Offline { get; } = offline;
    public bool Locked { get; } = locked;
}

/// <summary>
/// The POS API through the shell's signed-in HttpClient. Till calls also carry the till session
/// (<see cref="PosContract.TillHeader"/>) of whoever unlocked the till with their PIN.
/// </summary>
public sealed class PosApi(HttpClient http)
{
    public const string Base = PosContract.BasePath;

    /// <summary>The unlocked till's session token; null while it's locked.</summary>
    public string? TillToken { get; set; }

    public Task<T?> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path, null);

    public Task<T?> TillAsync<T>(HttpMethod method, string path, object? body = null) => SendAsync<T>(method, $"{Base}/till{path}", body, till: true);

    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, bool till = false)
    {
        HttpResponseMessage response;
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null)
                request.Content = JsonContent.Create(body, body.GetType(), options: ContractJson.Options);
            if (till && TillToken is { } token)
                request.Headers.Add(PosContract.TillHeader, token);
            response = await http.SendAsync(request);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            throw new PosException("No connection.", offline: true);
        }
        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NoContent)
                return default;
            if (response.IsSuccessStatusCode)
                return await response.Content.ReadFromJsonAsync<T>(ContractJson.Options);
            if (till && response.StatusCode == HttpStatusCode.Unauthorized)
                throw new PosException("The till is locked. Enter your PIN.", locked: true);
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout)
                throw new PosException("The server isn't answering. Try again in a moment.", offline: response.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout);
            throw new PosException(response.StatusCode switch
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

/// <summary>Money and quantities as the till shows them.</summary>
public static class PosFormat
{
    public static string Money(decimal amount, string currency) =>
        currency switch
        {
            "USD" => amount.ToString("$#,##0.00", CultureInfo.InvariantCulture),
            "ZAR" => amount.ToString("R #,##0.00", CultureInfo.InvariantCulture),
            _ => amount.ToString("#,##0.00", CultureInfo.InvariantCulture) + " " + currency
        };

    public static string Quantity(decimal quantity) => quantity.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Reads what someone typed as an amount; null if it isn't one.</summary>
    public static decimal? Parse(string? text) =>
        decimal.TryParse(text?.Trim().TrimStart('$').Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var value) ? value : null;
}
