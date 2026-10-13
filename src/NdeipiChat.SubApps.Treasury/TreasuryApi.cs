using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NdeipiChat.Contracts;

namespace NdeipiChat.SubApps.Treasury;

/// <summary>A message for the officer: the server's refusal (with any field problems), or no connection.</summary>
public sealed class TreasuryException(string message, string? code = null) : Exception(message)
{
    public string? Code { get; } = code;
}

/// <summary>Business Banking's API, through the shell's signed-in HttpClient. Shapes are snake_case, amounts decimal strings.</summary>
public sealed class TreasuryApi(HttpClient http)
{
    const string Base = TreasuryContract.BasePath;

    public async Task<T> GetAsync<T>(string path) => (await SendAsync<T>(HttpMethod.Get, path, null))!;

    /// <param name="idempotencyKey">For payments and cards: the same key for every retry of one request.</param>
    public async Task<T?> SendAsync<T>(HttpMethod method, string path, object? body, string? idempotencyKey = null)
    {
        using var response = await RawAsync(method, path, body, idempotencyKey);
        if (response.StatusCode == HttpStatusCode.NoContent)
            return default;
        return await response.Content.ReadFromJsonAsync<T>(TreasuryContract.Json);
    }

    public async Task<byte[]> DownloadAsync(string path)
    {
        using var response = await RawAsync(HttpMethod.Get, path, null, null);
        return await response.Content.ReadAsByteArrayAsync();
    }

    async Task<HttpResponseMessage> RawAsync(HttpMethod method, string path, object? body, string? idempotencyKey)
    {
        HttpResponseMessage response;
        try
        {
            var request = new HttpRequestMessage(method, $"{Base}/{path}");
            if (body is not null)
                request.Content = JsonContent.Create(body, body.GetType(), options: TreasuryContract.Json);
            if (idempotencyKey is not null)
                request.Headers.Add("Idempotency-Key", idempotencyKey);
            response = await http.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new TreasuryException("Can't reach the server. Check your connection.", "offline");
        }
        if (response.IsSuccessStatusCode)
            return response;
        using (response)
            throw await ErrorAsync(response);
    }

    static async Task<TreasuryException> ErrorAsync(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            return new("Your sign-in has expired. Reload the page.");
        if (response.StatusCode == HttpStatusCode.Forbidden && response.Content.Headers.ContentLength == 0)
            return new("Business Banking is for treasury officers. Ask an admin for the treasury role.");
        try
        {
            var error = await response.Content.ReadFromJsonAsync<TreasuryErrorDto>(TreasuryContract.Json);
            if (error is { Message.Length: > 0 })
            {
                var details = error.Details is { Count: > 0 } d ? " " + string.Join(" ", d.Select(x => x.Message)) : "";
                return new(error.Details is { Count: > 0 } ? details.Trim() : error.Message, error.Code);
            }
        }
        catch (JsonException)
        {
        }
        return new(response.StatusCode == HttpStatusCode.NotFound ? "That isn't here any more." : "Something went wrong. Please try again.");
    }
}

/// <summary>Where the officer is in the app; the root shows the matching page.</summary>
public enum TreasuryTab { Overview, Pay, Approvals, Payments, Payees, Cards, Assistant, Activity }

public static class Format
{
    public static string Money(decimal amount, string currency = "USD") => TreasuryContract.Money(amount, currency);

    public static string When(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy, HH:mm", CultureInfo.CurrentCulture);

    public static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);

    public static string Rail(string rail) => rail switch
    {
        "ach" => "ACH",
        "wire" => "Wire",
        "international" => "International",
        "crypto" => "USDC / crypto",
        _ => rail
    };

    public static string Status(string status) => status switch
    {
        "awaiting_confirmation" => "Draft: confirm to send",
        "pending_approval" => "Waiting for approval",
        "approved" => "Approved: sending",
        "bank_approval" => "Approve in Meow",
        "processing" => "Processing",
        "sent" => "Sent",
        "returned" => "Returned",
        "failed" => "Failed",
        "canceled" => "Canceled",
        "rejected" => "Rejected",
        "expired" => "Expired",
        "blocked" => "Blocked: sanctions",
        _ => status
    };

    public static string Tone(string status) => status switch
    {
        "sent" or "active" => "ok",
        "pending_approval" or "approved" or "bank_approval" or "processing" or "awaiting_confirmation" or "pending" or "frozen" => "wait",
        _ => "bad"
    };

    public static string Kind(string kind) => kind switch
    {
        "ach" => "🏦 ACH",
        "wire" => "🌐 Wire",
        "international" => "🌍 International",
        "crypto" => "🪙 Crypto",
        _ => kind
    };

    /// <summary>The rail a payee is paid on: its kind.</summary>
    public static string RailFor(string payeeKind) => payeeKind;
}
