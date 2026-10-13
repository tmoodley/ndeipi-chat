using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Treasury;

/// <summary>
/// Where the Ndeipi Enterprise Server's banking API is, and the gateway key this server uses to reach
/// it. The key goes in user-secrets or the environment (<c>Treasury__GatewayKey</c>), never in files.
/// </summary>
public sealed class TreasuryOptions
{
    public const string Section = "Treasury";

    /// <summary>The payments server's base address, such as <c>https://payments.ndeipi.com</c>.</summary>
    public string? PaymentsBaseUrl { get; set; }

    /// <summary>
    /// A gateway key from <c>dotnet run --project src/Ndeipi.Payments -- issue-gateway-key "Ndeipi app"</c>.
    /// It lets this server name the treasury officer on each request.
    /// </summary>
    public string? GatewayKey { get; set; }

    /// <summary>How often to check the treasury for changes to push to officers' screens.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsConfigured => PaymentsBaseUrl is { Length: > 0 } && GatewayKey is { Length: > 0 };
}

/// <summary>
/// Business Banking for treasury officers (SRS-NDE-MEOW-001). This server signs the officer in, checks
/// they hold the <c>treasury</c> role (through the launcher's sub-app check), and passes the request to
/// the Enterprise Server naming them; the Enterprise Server alone talks to Meow (SRS §2.2), keeps
/// every secret, and enforces approvals between officers.
/// </summary>
public static class TreasuryModule
{
    /// <summary>The banking operations an officer may reach, under <c>/ops/v1/banking/</c>. Anything else is refused here.</summary>
    static readonly Regex Allowed = new(
        @"^(settings|updates|journal|accounts|accounts/[^/]+/(transactions|statement\.csv)|payees|payees/[^/]+"
        + @"|payments|payments/[^/]+|payments/[^/]+/(approve|reject|cancel|confirm)|cards|cards/[^/]+|cards/[^/]+/(revoke|number)"
        + @"|assistant/sessions|assistant/sessions/[^/]+|assistant/sessions/[^/]+/messages)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public const string ClientName = "treasury";

    public static IServiceCollection AddTreasury(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TreasuryOptions>(configuration.GetSection(TreasuryOptions.Section));
        services.AddHttpClient(ClientName, (sp, http) =>
        {
            var options = sp.GetRequiredService<IOptions<TreasuryOptions>>().Value;
            if (options.PaymentsBaseUrl is { Length: > 0 } baseUrl)
                http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
            // The assistant may take a while on a hard question; everything else answers quickly.
            http.Timeout = TimeSpan.FromSeconds(120);
        });
        services.AddScoped<ITopicPolicy, TreasuryTopicPolicy>();
        services.AddHostedService<TreasuryRelay>();
        return services;
    }

    public static void MapTreasury(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + TreasuryContract.BasePath).RequireAuthorization().RequireSubApp(TreasuryContract.AppId);
        api.MapMethods("/{**path}", ["GET", "POST", "PATCH", "DELETE"], ForwardAsync).DisableAntiforgery();
    }

    static async Task<IResult> ForwardAsync(string path, HttpContext http, CurrentUserService users, IHttpClientFactory clients, IOptions<TreasuryOptions> options)
    {
        if (!options.Value.IsConfigured)
            return Results.Json(new { code = "banking_not_connected", message = "Business banking isn't connected on this server yet." }, statusCode: 503);
        var segments = path.Split('/');
        if (!Allowed.IsMatch(path) || segments.Any(s => s is "." or ".."))
            return Results.NotFound();

        var me = await users.GetAsync(http.User, http.RequestAborted);
        var target = "ops/v1/banking/" + string.Join('/', segments.Select(Uri.EscapeDataString)) + http.Request.QueryString;
        using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);
        request.Headers.Add("Operator-Key", options.Value.GatewayKey);
        // The officer, by their stable Ndeipi ID: approvals between officers are told apart by it.
        request.Headers.Add("Acting-Officer", $"{me.Id:N};{Uri.EscapeDataString(Officer(me))}");
        if (http.Request.Headers["Idempotency-Key"].ToString() is { Length: > 0 } key)
            request.Headers.Add("Idempotency-Key", key);
        if (http.Request.ContentLength > 0 || http.Request.Headers.TransferEncoding.Count > 0)
        {
            request.Content = new StreamContent(http.Request.Body);
            if (http.Request.ContentType is { } type)
                request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(type);
        }

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
        }
        catch (HttpRequestException)
        {
            return Results.Json(new { code = "banking_unreachable", message = "Business banking can't be reached right now. Try again shortly." }, statusCode: 503);
        }

        using (response)
        {
            http.Response.StatusCode = (int)response.StatusCode;
            // Card numbers must not be cached on the way to the officer.
            if (response.Headers.CacheControl is { } cache)
                http.Response.Headers.CacheControl = cache.ToString();
            if (response.Content.Headers.ContentDisposition is { } disposition)
                http.Response.Headers.ContentDisposition = disposition.ToString();
            if (response.Content.Headers.ContentType is { } contentType)
                http.Response.ContentType = contentType.ToString();
            await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
            return Results.Empty;
        }
    }

    static string Officer(User user) =>
        string.IsNullOrWhiteSpace(user.DisplayName) ? $"User {user.Id.ToString("N")[..8]}" : user.DisplayName.Trim();

    public static bool IsOfficer(User user) =>
        user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(TreasuryContract.Role, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Only treasury officers may follow the treasury's live updates.</summary>
public sealed class TreasuryTopicPolicy : ITopicPolicy
{
    public string Prefix => "treasury";

    public Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Task.FromResult(key == "all" && TreasuryModule.IsOfficer(user));
}

/// <summary>
/// Pushes "something changed" to officers' open Business Banking screens (FR-2.2). It watches the
/// treasury journal's cursor, which moves on every payment step, bank event and card change, and the
/// screens then reload what they show.
/// </summary>
public sealed class TreasuryRelay(IHttpClientFactory clients, IOptions<TreasuryOptions> options, ChatNotifier notifier, ILogger<TreasuryRelay> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.IsConfigured)
            return;
        long? last = null;
        using var timer = new PeriodicTimer(options.Value.PollInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "ops/v1/banking/updates");
                request.Headers.Add("Operator-Key", options.Value.GatewayKey);
                using var response = await clients.CreateClient(TreasuryModule.ClientName).SendAsync(request, stoppingToken);
                if (!response.IsSuccessStatusCode)
                    continue;
                var cursor = (await response.Content.ReadFromJsonAsync<TreasuryCursorDto>(TreasuryContract.Json, stoppingToken))!.Cursor;
                if (last is not null && cursor != last)
                    await notifier.PublishAsync(TreasuryContract.Topic, new TreasuryCursorDto(cursor));
                last = cursor;
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogDebug(e, "Treasury relay poll failed");
            }
        }
    }
}
