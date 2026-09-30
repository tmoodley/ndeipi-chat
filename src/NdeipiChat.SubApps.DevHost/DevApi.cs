using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NdeipiChat.SubApps.DevHost;

/// <summary>
/// A stand-in for the Ndeipi API: the routes the app calls, answered in memory. Patterns look like
/// minimal API routes, e.g. "api/bookings/{id}". A handler returns the response body (serialized as
/// the API would), null for 204 No Content, or a <see cref="DevResult"/> from <see cref="DevResults"/>
/// for anything else. Handlers may be async.
/// </summary>
public sealed class DevApi
{
    readonly List<Route> _routes = [];

    internal DevApi(DevRealtime realtime) => Realtime = realtime;

    internal DevRealtime Realtime { get; }

    internal DevUser? User { get; set; }

    /// <summary>How long each answer takes, so loading states show. 120 ms to start with.</summary>
    public TimeSpan Latency { get; set; } = TimeSpan.FromMilliseconds(120);

    /// <summary>
    /// The API's JSON settings: camelCase, case-insensitive reading, nulls left out, enums as
    /// strings (ContractJson in the Ndeipi API).
    /// </summary>
    public JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Each request the app made and how it was answered, for the dev host's Requests panel.</summary>
    internal event Action<DevApiCall>? Called;

    public DevApi MapGet(string pattern, Func<DevRequest, object?> handler) => Map("GET", pattern, handler);
    public DevApi MapGet(string pattern, Func<DevRequest, Task<object?>> handler) => Map("GET", pattern, handler);
    public DevApi MapPost(string pattern, Func<DevRequest, object?> handler) => Map("POST", pattern, handler);
    public DevApi MapPost(string pattern, Func<DevRequest, Task<object?>> handler) => Map("POST", pattern, handler);
    public DevApi MapPut(string pattern, Func<DevRequest, object?> handler) => Map("PUT", pattern, handler);
    public DevApi MapPut(string pattern, Func<DevRequest, Task<object?>> handler) => Map("PUT", pattern, handler);
    public DevApi MapPatch(string pattern, Func<DevRequest, object?> handler) => Map("PATCH", pattern, handler);
    public DevApi MapPatch(string pattern, Func<DevRequest, Task<object?>> handler) => Map("PATCH", pattern, handler);
    public DevApi MapDelete(string pattern, Func<DevRequest, object?> handler) => Map("DELETE", pattern, handler);
    public DevApi MapDelete(string pattern, Func<DevRequest, Task<object?>> handler) => Map("DELETE", pattern, handler);

    public DevApi Map(string method, string pattern, Func<DevRequest, object?> handler) =>
        Map(method, pattern, request => Task.FromResult(handler(request)));

    public DevApi Map(string method, string pattern, Func<DevRequest, Task<object?>> handler)
    {
        _routes.Add(new Route(method.ToUpperInvariant(), Segments(pattern), handler));
        return this;
    }

    internal async Task<HttpResponseMessage> HandleAsync(HttpRequestMessage message, string basePath, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var method = message.Method.Method.ToUpperInvariant();
        var uri = message.RequestUri!;
        var path = Uri.UnescapeDataString(uri.AbsolutePath);
        path = (path.StartsWith(basePath, StringComparison.OrdinalIgnoreCase) ? path[basePath.Length..] : path).Trim('/');
        var body = message.Content is null ? null : await message.Content.ReadAsStringAsync(ct);

        if (Latency > TimeSpan.Zero)
            await Task.Delay(Latency, ct);

        DevResult result;
        var (route, values) = Match(method, path);
        if (route is null)
        {
            var hint = values is null
                ? $"The dev host has no mock for {method} {path}. Add one in Program.cs: dev.Api.Map{Pascal(method)}(\"{path}\", request => ...)."
                : $"{path} has mocks, but none for {method}.";
            result = DevResults.Problem(values is null ? 404 : 405, hint);
        }
        else
        {
            var request = new DevRequest(method, path, values!, Query(uri.Query), Headers(message), body, User!, Realtime, Json);
            try
            {
                result = ToResult(await Unwrap(await route.Handler(request)));
            }
            catch (Exception ex)
            {
                result = DevResults.Problem(500, $"The mock for {method} {path} threw {ex.GetType().Name}: {ex.Message}");
            }
        }

        var responseBody = result.Body switch
        {
            null => null,
            string text when result.ContentType != "application/json" => text,
            var value => JsonSerializer.Serialize(value, value.GetType(), Json),
        };
        var response = new HttpResponseMessage((HttpStatusCode)result.Status) { RequestMessage = message };
        if (responseBody is not null)
            response.Content = new StringContent(responseBody, Encoding.UTF8, result.ContentType);

        Called?.Invoke(new DevApiCall(method, path + uri.Query, result.Status, (int)watch.ElapsedMilliseconds, body, responseBody));
        return response;
    }

    (Route? Route, Dictionary<string, string>? Values) Match(string method, string path)
    {
        var segments = Segments(path);
        Dictionary<string, string>? pathMatched = null;
        foreach (var route in _routes)
        {
            if (TryMatch(route.Segments, segments) is not { } values)
                continue;
            if (route.Method == method)
                return (route, values);
            pathMatched = values;
        }
        return (null, pathMatched);
    }

    static Dictionary<string, string>? TryMatch(string[] pattern, string[] path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < pattern.Length; i++)
        {
            var part = pattern[i];
            if (part.StartsWith("{*") && part.EndsWith('}'))
            {
                values[part[2..^1]] = string.Join('/', path.Skip(i));
                return values;
            }
            if (i >= path.Length)
                return null;
            if (part.StartsWith('{') && part.EndsWith('}'))
                values[part[1..^1].Split(':')[0]] = path[i];
            else if (!part.Equals(path[i], StringComparison.OrdinalIgnoreCase))
                return null;
        }
        return pattern.Length == path.Length ? values : null;
    }

    static string[] Segments(string path) => path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

    static string Pascal(string method) => method[0] + method[1..].ToLowerInvariant();

    static Dictionary<string, string> Query(string query) =>
        query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .GroupBy(pair => Uri.UnescapeDataString(pair[0]), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => Uri.UnescapeDataString((g.Last().ElementAtOrDefault(1) ?? "").Replace('+', ' ')), StringComparer.OrdinalIgnoreCase);

    static Dictionary<string, string> Headers(HttpRequestMessage message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<KeyValuePair<string, IEnumerable<string>>> all = message.Headers;
        if (message.Content is not null)
            all = all.Concat(message.Content.Headers);
        foreach (var (name, values) in all)
            headers[name] = string.Join(", ", values);
        return headers;
    }

    // A handler written as `r => store.ListAsync()` returns a Task through the object? overload.
    static async Task<object?> Unwrap(object? value)
    {
        if (value is not Task task)
            return value;
        await task;
        var type = task.GetType();
        return type.IsGenericType ? type.GetProperty("Result")!.GetValue(task) : null;
    }

    static DevResult ToResult(object? value) => value switch
    {
        DevResult result => result,
        null => DevResults.NoContent(),
        _ => DevResults.Ok(value),
    };

    sealed record Route(string Method, string[] Segments, Func<DevRequest, Task<object?>> Handler);
}

/// <summary>A request the app made, as a mock sees it.</summary>
public sealed class DevRequest(
    string method,
    string path,
    IReadOnlyDictionary<string, string> routeValues,
    IReadOnlyDictionary<string, string> query,
    IReadOnlyDictionary<string, string> headers,
    string? body,
    DevUser user,
    DevRealtime realtime,
    JsonSerializerOptions json)
{
    public string Method { get; } = method;

    /// <summary>The path without the query, e.g. "api/bookings/42".</summary>
    public string Path { get; } = path;

    public IReadOnlyDictionary<string, string> RouteValues { get; } = routeValues;

    public IReadOnlyDictionary<string, string> Query { get; } = query;

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    /// <summary>The request body as sent, or null.</summary>
    public string? Body { get; } = body;

    /// <summary>Who the app is signed in as.</summary>
    public DevUser User { get; } = user;

    /// <summary>Publish to the app's topics, e.g. after a change: <c>await request.Realtime.PublishAsync("bookings:all", booking)</c>.</summary>
    public DevRealtime Realtime { get; } = realtime;

    /// <summary>A route value, e.g. Route("id") for "api/bookings/{id}".</summary>
    public string Route(string name) =>
        RouteValues.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException($"The route has no {{{name}}}.");

    /// <summary>The body read as JSON the way the API reads it.</summary>
    public T? ReadJson<T>() => string.IsNullOrEmpty(Body) ? default : JsonSerializer.Deserialize<T>(Body, json);
}

/// <summary>A response other than 200 with a body or 204 without one.</summary>
public sealed record DevResult(int Status, object? Body, string ContentType = "application/json");

public static class DevResults
{
    public static DevResult Ok(object? body) => new(200, body);

    public static DevResult Created(object? body) => new(201, body);

    public static DevResult NoContent() => new(204, null);

    /// <summary>
    /// An error the way the Ndeipi API sends one: problem details whose title is written for the
    /// person using the app.
    /// </summary>
    public static DevResult Problem(int status, string title) =>
        new(status, new { type = "about:blank", title, status }, "application/problem+json");

    public static DevResult BadRequest(string title) => Problem(400, title);

    public static DevResult Unauthorized(string title = "Your sign-in has expired.") => Problem(401, title);

    /// <summary>What the API says when the user can't use the app, or this part of it.</summary>
    public static DevResult Forbidden(string title = "You don't have access to this.") => Problem(403, title);

    public static DevResult NotFound(string title = "That isn't here any more.") => Problem(404, title);

    public static DevResult Conflict(string title) => Problem(409, title);
}

/// <summary>One call, for the Requests panel.</summary>
internal sealed record DevApiCall(string Method, string Path, int Status, int Milliseconds, string? RequestBody, string? ResponseBody);

/// <summary>Sends the app's API calls to the <see cref="DevApi"/> mocks instead of the network.</summary>
internal sealed class DevApiHandler(DevApi api, string basePath) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        api.HandleAsync(request, basePath, cancellationToken);
}
