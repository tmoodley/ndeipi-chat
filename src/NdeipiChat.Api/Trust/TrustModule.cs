using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Gigs;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Trust;

/// <summary>The Trust Score: everyone's 0-100 score, and the page where people link accounts to raise it.</summary>
public static class TrustModule
{
    public static IServiceCollection AddTrust(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TrustOptions>(configuration.GetSection(TrustOptions.Section));
        services.AddMemoryCache();
        services.AddSingleton<TrustTokenProtector>();
        services.AddHttpClient<TrustOAuthClient>(http => http.Timeout = TimeSpan.FromSeconds(20));
        services.AddScoped<TrustScorer>();
        services.AddScoped<TrustService>();
        services.AddScoped<IBankingStatusListener, TrustBankingListener>();
        services.AddScoped<IBankTransferListener, TrustTransferListener>();
        services.AddScoped<IWorkRecordListener, TrustWorkListener>();
        services.AddScoped<IUserProfileListener, TrustSignInSync>();
        services.AddScoped<ITopicPolicy, TrustTopicPolicy>();
        services.AddHostedService<TrustRecheckWorker>();
        return services;
    }

    public static void MapTrust(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + TrustContract.BasePath).RequireAuthorization();

        api.MapGet("/settings", (TrustService trust) => Results.Ok(trust.Settings()));

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, TrustService trust) =>
            Results.Ok(await trust.MeAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapPost("/refresh", async (HttpContext http, CurrentUserService users, TrustService trust) =>
            Results.Ok(await trust.RefreshAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapGet("/{userId:guid}", async (Guid userId, HttpContext http, TrustService trust) =>
            await trust.ScoreAsync(userId, http.RequestAborted) is { } score ? Results.Ok(score) : Results.NotFound());

        api.MapPost("/links/telegram", async (TelegramLoginRequest login, HttpContext http, CurrentUserService users, TrustService trust) =>
            Results.Ok(await trust.LinkTelegramAsync(await users.GetAsync(http.User, http.RequestAborted), login, http.RequestAborted)));

        api.MapPost("/links/{platform}", async (string platform, HttpContext http, CurrentUserService users, TrustService trust) =>
            Results.Ok(trust.StartLink(await users.GetAsync(http.User, http.RequestAborted), platform, SiteFor(http.Request))));

        api.MapDelete("/links/{platform}", async (string platform, HttpContext http, CurrentUserService users, TrustService trust) =>
            Results.Ok(await trust.UnlinkAsync(await users.GetAsync(http.User, http.RequestAborted), platform, http.RequestAborted)));

        // The platform sends the browser back here, without our sign-in token: the state says who it was.
        app.MapGet("/" + TrustContract.BasePath + "/oauth/{platform}/callback",
            async (string platform, string? code, string? state, string? error, HttpContext http, TrustService trust) =>
            {
                var problem = await trust.CompleteLinkAsync(platform, code, state, error, http.RequestAborted);
                return Results.Redirect(trust.ReturnUrl(SiteFor(http.Request), platform, problem));
            }).AllowAnonymous();
    }

    static Uri SiteFor(HttpRequest request) => new($"{request.Scheme}://{request.Host}{request.PathBase}/");
}

/// <summary>Anyone may follow anyone's score: it's on their profile for everyone.</summary>
public sealed class TrustTopicPolicy(ChatDbContext db) : ITopicPolicy
{
    public string Prefix => "trust";

    public async Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Guid.TryParseExact(key, "N", out var id) && await db.Users.AnyAsync(u => u.Id == id, ct);
}

/// <summary>Tier 1: Bridge's identity check passed (or was withdrawn).</summary>
public sealed class TrustBankingListener(TrustScorer scorer) : IBankingStatusListener
{
    public async Task BankingStatusChangedAsync(Guid userId, BankingStatusDto status, CancellationToken ct) =>
        await scorer.RecomputeAsync(userId, ct);
}

/// <summary>History: a settlement completed, so both sides may have a new counterparty.</summary>
public sealed class TrustTransferListener(TrustScorer scorer) : IBankTransferListener
{
    public async Task TransferChangedAsync(BankTransfer transfer, CancellationToken ct)
    {
        if (transfer.Status != TransferStatuses.Confirmed || transfer.SenderId == transfer.RecipientId)
            return;
        await scorer.RecomputeAsync(transfer.SenderId, ct);
        await scorer.RecomputeAsync(transfer.RecipientId, ct);
    }
}

/// <summary>Re-checks linked accounts and applies decay, once soon after start and then every interval.</summary>
public sealed class TrustRecheckWorker(IServiceScopeFactory scopes, Microsoft.Extensions.Options.IOptions<TrustOptions> options, ILogger<TrustRecheckWorker> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<TrustService>().RecheckAllAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "The Trust Score re-check failed; will try again next time");
                }
                await Task.Delay(options.Value.RecheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
