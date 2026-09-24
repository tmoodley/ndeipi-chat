using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ndeipi.Api.Auth;

/// <summary>The parts of Clerk's Backend API this server uses.</summary>
public interface IClerkBackendApi
{
    Task<ClerkUser?> GetUserAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Mints a fresh session token for an existing Clerk session. Throws
    /// <see cref="ClerkSessionEndedException"/> once the session is signed out, revoked or expired --
    /// which is how signing out in Clerk also signs the app out.
    /// </summary>
    Task<string> CreateSessionTokenAsync(string sessionId, string? template, CancellationToken ct);

    Task RevokeSessionAsync(string sessionId, CancellationToken ct);
}

public sealed class ClerkSessionEndedException(string sessionId)
    : Exception($"Clerk session {sessionId} is no longer active.");

public sealed record ClerkEmailAddress(string Id, string EmailAddress);

public sealed record ClerkUser(
    string Id,
    string? FirstName,
    string? LastName,
    string? Username,
    string? ImageUrl,
    string? PrimaryEmailAddressId,
    List<ClerkEmailAddress>? EmailAddresses)
{
    public string? PrimaryEmail =>
        EmailAddresses?.FirstOrDefault(e => e.Id == PrimaryEmailAddressId)?.EmailAddress
        ?? EmailAddresses?.FirstOrDefault()?.EmailAddress;

    public string? FullName
    {
        get
        {
            var name = $"{FirstName} {LastName}".Trim();
            return name.Length > 0 ? name : null;
        }
    }
}

public sealed class ClerkBackendApi(HttpClient http) : IClerkBackendApi
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public async Task<ClerkUser?> GetUserAsync(string userId, CancellationToken ct)
    {
        using var response = await http.GetAsync($"users/{Uri.EscapeDataString(userId)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClerkUser>(Json, ct);
    }

    public async Task<string> CreateSessionTokenAsync(string sessionId, string? template, CancellationToken ct)
    {
        var path = $"sessions/{Uri.EscapeDataString(sessionId)}/tokens";
        if (!string.IsNullOrWhiteSpace(template))
            path += "/" + Uri.EscapeDataString(template);

        using var response = await http.PostAsJsonAsync(path, new { }, Json, ct);
        if ((int)response.StatusCode is >= 400 and < 500 && response.StatusCode is not HttpStatusCode.TooManyRequests)
            throw new ClerkSessionEndedException(sessionId);
        response.EnsureSuccessStatusCode();

        var token = await response.Content.ReadFromJsonAsync<TokenBody>(Json, ct);
        return token?.Jwt ?? throw new InvalidOperationException("Clerk returned no token.");
    }

    public async Task RevokeSessionAsync(string sessionId, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync($"sessions/{Uri.EscapeDataString(sessionId)}/revoke", new { }, Json, ct);
        if (response.StatusCode is not HttpStatusCode.NotFound)
            response.EnsureSuccessStatusCode();
    }

    sealed record TokenBody(string Jwt);
}
