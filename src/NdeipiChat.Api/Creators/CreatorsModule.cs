using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Creators;

/// <summary>The Creators sub-app's API (NDEIPI-SRS-CREATOR-001), behind the launcher's access check.</summary>
public static class CreatorsModule
{
    public static IServiceCollection AddCreators(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CreatorsOptions>(configuration.GetSection(CreatorsOptions.Section));
        services.AddScoped<CreatorMediaService>();
        services.AddScoped<CreatorService>();
        services.AddScoped<CreatorBilling>();
        services.AddScoped<PayoutService>();
        services.AddScoped<IBankTransferListener, CreatorSettlement>();
        services.AddScoped<ITopicPolicy, CreatorTopicPolicy>();
        services.AddHostedService<CreatorsWorker>();
        return services;
    }

    public static void MapCreators(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + CreatorsContract.BasePath).RequireAuthorization().RequireSubApp(CreatorsContract.AppId);
        static async Task<User> Me(HttpContext http, CurrentUserService users) => await users.GetAsync(http.User, http.RequestAborted);

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.MeAsync(await Me(http, users), http.RequestAborted)));

        api.MapGet("/", async (string? q, string? category, HttpContext http, CreatorService creators) =>
            Results.Ok(await creators.DiscoverAsync(q, category, http.RequestAborted)));

        api.MapGet("/{creatorId:guid}", async (Guid creatorId, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Found(await creators.StorefrontAsync(await Me(http, users), creatorId, http.RequestAborted)));

        api.MapGet("/{creatorId:guid}/posts", async (Guid creatorId, DateTimeOffset? before, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Found(await creators.PostsAsync(await Me(http, users), creatorId, before, http.RequestAborted)));

        api.MapGet("/posts/{postId:guid}", async (Guid postId, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Found(await creators.PostAsync(await Me(http, users), postId, http.RequestAborted)));

        api.MapPost("/posts/{postId:guid}/play", async (Guid postId, HttpContext http, CurrentUserService users, CreatorService creators) =>
        {
            await creators.PlayedAsync(await Me(http, users), postId, http.RequestAborted);
            return Results.NoContent();
        });

        // ---- Paying ----

        api.MapPost("/posts/{postId:guid}/unlock", async (Guid postId, HttpContext http, CurrentUserService users, CreatorBilling billing) =>
            Found(await billing.UnlockAsync(await Me(http, users), postId, http.RequestAborted)));

        api.MapPost("/{creatorId:guid}/subscribe", async (Guid creatorId, CreatorSubscribeRequest request, HttpContext http, CurrentUserService users, CreatorBilling billing) =>
            Results.Ok(await billing.SubscribeAsync(await Me(http, users), creatorId, request, http.RequestAborted)));

        api.MapPost("/subscriptions/{id:guid}/cancel", async (Guid id, HttpContext http, CurrentUserService users, CreatorBilling billing) =>
            Found(await billing.CancelAsync(await Me(http, users), id, http.RequestAborted)));

        api.MapPost("/subscriptions/{id:guid}/resume", async (Guid id, HttpContext http, CurrentUserService users, CreatorBilling billing) =>
            Found(await billing.ResumeAsync(await Me(http, users), id, http.RequestAborted)));

        // ---- The studio ----

        api.MapPut("/profile", async (SaveCreatorRequest request, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.SaveProfileAsync(await Me(http, users), request, http.RequestAborted)));

        api.MapPost("/profile/banner", async (HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.SetBannerAsync(await Me(http, users), await FileAsync(http), http.RequestAborted)))
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(CreatorsContract.MaxImageBytes + 1024 * 1024));

        api.MapGet("/studio/tiers", async (HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.TiersAsync(await Me(http, users), http.RequestAborted)));

        api.MapPost("/studio/tiers", async (SaveCreatorTierRequest request, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Found(await creators.SaveTierAsync(await Me(http, users), null, request, http.RequestAborted)));

        api.MapPut("/studio/tiers/{id:guid}", async (Guid id, SaveCreatorTierRequest request, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Found(await creators.SaveTierAsync(await Me(http, users), id, request, http.RequestAborted)));

        api.MapPost("/studio/media", async (HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.UploadAsync(await Me(http, users), await FileAsync(http), http.RequestAborted)))
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(CreatorsContract.MaxAudioVideoBytes + 1024 * 1024));

        api.MapPost("/studio/posts", async (SaveCreatorPostRequest request, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Found(await creators.SavePostAsync(await Me(http, users), null, request, http.RequestAborted)));

        api.MapPut("/studio/posts/{id:guid}", async (Guid id, SaveCreatorPostRequest request, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Found(await creators.SavePostAsync(await Me(http, users), id, request, http.RequestAborted)));

        api.MapDelete("/studio/posts/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, CreatorService creators) =>
            await creators.DeletePostAsync(await Me(http, users), id, http.RequestAborted) ? Results.NoContent() : Results.NotFound());

        api.MapGet("/studio/earnings", async (HttpContext http, CurrentUserService users, CreatorBilling billing) =>
            Results.Ok(await billing.EarningsAsync(await Me(http, users), http.RequestAborted)));

        api.MapGet("/studio/subscribers", async (HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.SubscribersAsync(await Me(http, users), http.RequestAborted)));

        api.MapPost("/studio/broadcasts", async (CreatorBroadcastRequest request, HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.BroadcastAsync(await Me(http, users), request, http.RequestAborted)));

        api.MapGet("/studio/analytics", async (HttpContext http, CurrentUserService users, CreatorService creators) =>
            Results.Ok(await creators.AnalyticsAsync(await Me(http, users), http.RequestAborted)));

        api.MapGet("/studio/payouts", async (HttpContext http, CurrentUserService users, PayoutService payouts) =>
            Results.Ok(await payouts.PayoutsAsync(await Me(http, users), http.RequestAborted)));

        api.MapPost("/studio/payout-accounts", async (SavePayoutAccountRequest request, HttpContext http, CurrentUserService users, PayoutService payouts) =>
            Results.Ok(await payouts.AddAccountAsync(await Me(http, users), request, http.RequestAborted)));

        api.MapDelete("/studio/payout-accounts/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PayoutService payouts) =>
            await payouts.RemoveAccountAsync(await Me(http, users), id, http.RequestAborted) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/studio/withdrawals", async (WithdrawRequest request, HttpContext http, CurrentUserService users, PayoutService payouts) =>
            Results.Ok(await payouts.WithdrawAsync(await Me(http, users), request, http.RequestAborted)));

        // An expiring link to a photo, audio or video (NFR-CR-06): the token is the permission, so
        // <img>, <audio> and <video> can load it without the sign-in token.
        app.MapGet("/" + CreatorsContract.BasePath + "/media/{token}", async (string token, HttpContext http, CreatorMediaService media) =>
        {
            if (await media.OpenAsync(token, http.RequestAborted) is not { } file)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=900";
            return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        }).AllowAnonymous();
    }

    static async Task<IFormFile> FileAsync(HttpContext http)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        return form.Files.GetFile("file") ?? throw new ChatRejectedException("Choose a file to upload.");
    }

    static IResult Found<T>(T? value) where T : class => value is null ? Results.NotFound() : Results.Ok(value);
}

/// <summary>Each person follows only their own creator news (their subscriptions, or their payments as a creator).</summary>
public sealed class CreatorTopicPolicy : ITopicPolicy
{
    public string Prefix => "creator";

    public Task<bool> CanSubscribeAsync(User user, string key, CancellationToken ct) =>
        Task.FromResult(Guid.TryParseExact(key, "N", out var id) && id == user.Id);
}

/// <summary>Renewals and grace periods (FR-CR-06, 07) and withdrawals on their way (FR-CR-13), every few minutes.</summary>
public sealed class CreatorsWorker(IServiceScopeFactory scopes, IOptions<CreatorsOptions> options, ILogger<CreatorsWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<CreatorBilling>().RenewDueAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<PayoutService>().SyncAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogError(ex, "The creators check failed; will try again");
                }
                await Task.Delay(options.Value.CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
