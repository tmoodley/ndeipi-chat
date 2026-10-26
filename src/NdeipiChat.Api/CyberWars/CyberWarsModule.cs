using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.CyberWars;

/// <summary>
/// CyberWars: GN Mining's API. Orders go up over HTTP, because a sub-app can only listen on the
/// shell's SignalR connection; state comes down on the player's topic every tick (SRS section 10).
/// </summary>
public static class CyberWarsModule
{
    public static IServiceCollection AddCyberWars(this IServiceCollection services)
    {
        services.AddSingleton<CyberWarsMatches>();
        services.AddHostedService<CyberWarsLoop>();
        services.AddScoped<CyberWarsService>();
        services.AddScoped<ITopicPolicy, CyberWarsTopicPolicy>();
        services.AddScoped<CyberWarsWorldService>();
        services.AddScoped<CyberWarsCards>();
        services.AddScoped<IChatCommandListener, CyberWarsWarRoom>();
        services.AddMessageKind<CyberWarsCardHandler>();
        return services;
    }

    public static void MapCyberWars(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + CyberWarsContract.BasePath).RequireAuthorization().RequireSubApp(CyberWarsContract.AppId);

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, CyberWarsService game) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await game.MeAsync(me, http.RequestAborted));
        });

        api.MapPost("/matches", async (StartMatchRequest request, HttpContext http, CurrentUserService users, CyberWarsService game) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await game.StartAsync(me, request, http.RequestAborted));
        });

        api.MapGet("/matches/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, CyberWarsService game) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await game.StateAsync(me, id, http.RequestAborted) is { } state ? Results.Ok(state) : Results.NotFound();
        });

        api.MapPost("/matches/{id:guid}/orders", async (Guid id, OrderBatchRequest request, HttpContext http, CurrentUserService users, CyberWarsService game) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await game.OrdersAsync(me, id, request, http.RequestAborted) is { } results ? Results.Ok(results) : Results.NotFound();
        });

        api.MapPost("/matches/{id:guid}/presence", async (Guid id, HttpContext http, CurrentUserService users, CyberWarsService game) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await game.PresenceAsync(me, id, http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/matches/{id:guid}/end", async (Guid id, HttpContext http, CurrentUserService users, CyberWarsService game) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await game.EndAsync(me, id, http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });

        // ---- Phase 02: the world map, alliances and raids ----

        api.MapGet("/map", async (HttpContext http, CyberWarsWorldService world) =>
            Results.Ok(await world.MapAsync(http.RequestAborted)));

        api.MapPost("/home", async (ClaimHomeRequest request, HttpContext http, CurrentUserService users, CyberWarsWorldService world) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await world.ClaimHomeAsync(me, request, http.RequestAborted));
        });

        api.MapPost("/raids", async (StartRaidRequest request, HttpContext http, CurrentUserService users, CyberWarsService game) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await game.RaidAsync(me, request, http.RequestAborted));
        });

        api.MapGet("/alliances", async (ZambianProvince? province, HttpContext http, CyberWarsWorldService world) =>
            Results.Ok(await world.AlliancesAsync(province, http.RequestAborted)));

        api.MapPost("/alliances", async (CreateAllianceRequest request, HttpContext http, CurrentUserService users, CyberWarsWorldService world) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await world.CreateAllianceAsync(me, request, http.RequestAborted));
        });

        api.MapPost("/alliances/{id:guid}/join", async (Guid id, HttpContext http, CurrentUserService users, CyberWarsWorldService world) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await world.JoinAsync(me, id, http.RequestAborted) is { } alliance ? Results.Ok(alliance) : Results.NotFound();
        });

        api.MapPost("/alliances/{id:guid}/leave", async (Guid id, HttpContext http, CurrentUserService users, CyberWarsWorldService world) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await world.LeaveAsync(me, id, http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/pings", async (AlliancePingRequest request, HttpContext http, CurrentUserService users, CyberWarsWorldService world) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            await world.PingAsync(me, request, http.RequestAborted);
            return Results.NoContent();
        });
    }
}
