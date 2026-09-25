using NdeipiChat.Api.Auth;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Shamwaris;

public static class ShamwariModule
{
    public static IServiceCollection AddShamwaris(this IServiceCollection services) =>
        services.AddScoped<ShamwariService>();

    public static void MapShamwaris(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/shamwaris").RequireAuthorization();

        api.MapGet("", async (HttpContext http, CurrentUserService users, ShamwariService shamwaris) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await shamwaris.ListAsync(me.Id, http.RequestAborted));
        });

        api.MapPost("", async (AddShamwariRequest request, HttpContext http, CurrentUserService users, ShamwariService shamwaris) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await shamwaris.AddAsync(me, request.Contact, http.RequestAborted));
        });

        api.MapPost("/requests/{id:guid}/accept", async (Guid id, HttpContext http, CurrentUserService users, ShamwariService shamwaris) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await shamwaris.AcceptAsync(me, id, http.RequestAborted));
        });

        // Declines a request to you, or cancels one you sent.
        api.MapDelete("/requests/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, ShamwariService shamwaris) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await shamwaris.DeleteRequestAsync(me, id, http.RequestAborted));
        });

        api.MapDelete("/{userId:guid}", async (Guid userId, HttpContext http, CurrentUserService users, ShamwariService shamwaris) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await shamwaris.RemoveAsync(me, userId, http.RequestAborted));
        });
    }
}
