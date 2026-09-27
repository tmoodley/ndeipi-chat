using Microsoft.Extensions.Options;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Gigs;

/// <summary>The Gigs sub-app's API. The app is a Razor Class Library the shell loads on first use.</summary>
public static class GigsModule
{
    public static IServiceCollection AddGigs(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<GigsOptions>(config.GetSection(GigsOptions.Section));
        services.AddScoped<GigsService>();
        services.AddScoped<ITopicPolicy, GigUserTopicPolicy>();
        services.AddScoped<ITopicPolicy, GigMapTopicPolicy>();
        return services;
    }

    public static void MapGigs(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + GigsContract.BasePath).RequireAuthorization().RequireSubApp(GigsContract.AppId);

        api.MapGet("/settings", (IOptions<GigsOptions> options) => Results.Ok(new { tokenSymbol = options.Value.TokenSymbol }));

        // ---- Profiles ----

        api.MapGet("/profile", async (HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await gigs.ProfileAsync(me, me.Id, http.RequestAborted) is { } p ? Results.Ok(p) : Results.NoContent();
        });

        api.MapGet("/profiles/{userId:guid}", async (Guid userId, HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await gigs.ProfileAsync(me, userId, http.RequestAborted) is { } p ? Results.Ok(p) : Results.NotFound();
        });

        api.MapPut("/profile", async (SaveGigProfileRequest request, HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await gigs.SaveProfileAsync(me, request, http.RequestAborted));
        });

        api.MapPut("/profile/availability", async (AvailabilityRequest request, HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await gigs.SetAvailabilityAsync(me, request, http.RequestAborted));
        });

        api.MapGet("/map", async (HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await gigs.MapAsync(me, http.RequestAborted));
        });

        // ---- Gigs ----

        api.MapGet("", async (bool? active, HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await gigs.MineAsync(me, active ?? false, http.RequestAborted));
        });

        api.MapPost("", async (CreateGigRequest request, HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await gigs.CreateAsync(me, request, http.RequestAborted));
        });

        api.MapGet("/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await gigs.GetAsync(me, id, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound();
        });

        Action("accept", (g, me, id, ct) => g.AcceptAsync(me, id, ct));
        Action("decline", (g, me, id, ct) => g.DeclineAsync(me, id, ct));
        Action("submit", (g, me, id, ct) => g.SubmitAsync(me, id, ct));
        Action("approve", (g, me, id, ct) => g.ApproveAsync(me, id, ct));
        Action("cancel", (g, me, id, ct) => g.CancelAsync(me, id, ct));

        api.MapPost("/{id:guid}/rating", async (Guid id, RateGigRequest request, HttpContext http, CurrentUserService users, GigsService gigs) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await gigs.RateAsync(me, id, request.Stars, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound();
        });

        void Action(string name, Func<GigsService, Data.User, Guid, CancellationToken, Task<GigDto?>> act) =>
            api.MapPost($"/{{id:guid}}/{name}", async (Guid id, HttpContext http, CurrentUserService users, GigsService gigs) =>
            {
                var me = await users.GetAsync(http.User, http.RequestAborted);
                return await act(gigs, me, id, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound();
            });
    }
}
