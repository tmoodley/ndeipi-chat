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
    }
}
