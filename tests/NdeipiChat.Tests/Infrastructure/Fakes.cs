using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using NdeipiChat.Api.Auth;

namespace NdeipiChat.Tests.Infrastructure;

/// <summary>Clerk-shaped session tokens, signed with a key only the tests (and the test API) know.</summary>
public static class TestTokens
{
    public static readonly RsaSecurityKey Key = new(CreateRsa()) { KeyId = "test-key" };

    /// <summary>
    /// An RSA key that exists now. On Windows, RSA.Create generates the key lazily on first use.
    /// Test classes run in parallel, so two of them touching a shared key at once can each trigger
    /// generation, leaving the servers with different keys.
    /// </summary>
    public static RSA CreateRsa()
    {
        var rsa = RSA.Create(2048);
        rsa.ExportParameters(includePrivateParameters: false);
        return rsa;
    }

    public static string Create(
        string userId,
        string sessionId,
        TimeSpan? lifetime = null,
        string issuer = TestApp.Issuer,
        string? azp = null,
        SecurityKey? key = null)
    {
        var expires = DateTime.UtcNow + (lifetime ?? TimeSpan.FromMinutes(5));
        var issued = expires < DateTime.UtcNow ? expires.AddMinutes(-5) : DateTime.UtcNow.AddSeconds(-5);
        // Clerk gives every token a jti; without one, two tokens minted in the same second are identical.
        var claims = new Dictionary<string, object> { ["sub"] = userId, ["sid"] = sessionId, ["jti"] = Guid.NewGuid().ToString("N") };
        if (azp is not null)
            claims["azp"] = azp;

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = claims,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = expires,
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256)
        });
    }
}

/// <summary>Clerk's Backend API: users, and sessions that can be ended.</summary>
public sealed class FakeClerk : IClerkBackendApi
{
    readonly ConcurrentDictionary<string, ClerkUser> _users = new();
    readonly ConcurrentDictionary<string, (string UserId, bool Active)> _sessions = new();

    public void AddUser(ClerkUser user) => _users[user.Id] = user;

    public string StartSession(string userId)
    {
        var sessionId = "sess_" + Guid.NewGuid().ToString("N")[..16];
        _sessions[sessionId] = (userId, true);
        return sessionId;
    }

    public void EndSession(string sessionId)
    {
        if (_sessions.TryGetValue(sessionId, out var session))
            _sessions[sessionId] = session with { Active = false };
    }

    public bool IsActive(string sessionId) => _sessions.TryGetValue(sessionId, out var s) && s.Active;

    public Task<ClerkUser?> GetUserAsync(string userId, CancellationToken ct) =>
        Task.FromResult(_users.GetValueOrDefault(userId));

    public Task<string> CreateSessionTokenAsync(string sessionId, string? template, CancellationToken ct) =>
        _sessions.TryGetValue(sessionId, out var s) && s.Active
            ? Task.FromResult(TestTokens.Create(s.UserId, sessionId))
            : throw new ClerkSessionEndedException(sessionId);

    public Task RevokeSessionAsync(string sessionId, CancellationToken ct)
    {
        EndSession(sessionId);
        return Task.CompletedTask;
    }
}

public sealed record RecordedRequest(HttpMethod Method, string Path, string? Body, string? IdempotencyKey, string? ApiKey)
{
    public JsonElement Json => JsonDocument.Parse(Body ?? "null").RootElement;
}

/// <summary>Bridge's API: canned responses by method and path, with every request recorded.</summary>
public sealed class StubBridge : HttpMessageHandler
{
    readonly ConcurrentDictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes = new();

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public void On(HttpMethod method, string path, Func<RecordedRequest, HttpResponseMessage> respond) =>
        _routes[$"{method} {path}"] = respond;

    public void OnJson(HttpMethod method, string path, object body, HttpStatusCode status = HttpStatusCode.OK) =>
        On(method, path, _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        });

    public IReadOnlyList<RecordedRequest> RequestsTo(HttpMethod method, string path) =>
        Requests.Where(r => r.Method == method && r.Path == path).ToList();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var recorded = new RecordedRequest(
            request.Method,
            request.RequestUri!.AbsolutePath,
            request.Content is null ? null : await request.Content.ReadAsStringAsync(ct),
            request.Headers.TryGetValues("Idempotency-Key", out var keys) ? keys.First() : null,
            request.Headers.TryGetValues("Api-Key", out var apiKeys) ? apiKeys.First() : null);
        Requests.Enqueue(recorded);

        return _routes.TryGetValue($"{request.Method} {recorded.Path}", out var respond)
            ? respond(recorded)
            : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"message\":\"no stub\"}") };
    }
}

public static class Wait
{
    /// <summary>Polls until the condition holds -- for effects that arrive asynchronously.</summary>
    public static async Task UntilAsync(Func<bool> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting until {because}.");
            await Task.Delay(50);
        }
    }

    public static async Task UntilAsync(Func<Task<bool>> condition, string because, int timeoutMs = 10_000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Timed out waiting until {because}.");
            await Task.Delay(50);
        }
    }

    /// <summary>The next hub event of the given name that matches.</summary>
    public static Task<T> ForEventAsync<T>(HubConnection hub, string method, Func<T, bool>? match = null, int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        IDisposable? subscription = null;
        subscription = hub.On<T>(method, value =>
        {
            if (match is null || match(value))
            {
                tcs.TrySetResult(value);
                subscription?.Dispose();
            }
        });

        var timeout = new CancellationTokenSource(timeoutMs);
        timeout.Token.Register(() => tcs.TrySetException(new TimeoutException($"No {method} event arrived.")));
        return tcs.Task;
    }
}
