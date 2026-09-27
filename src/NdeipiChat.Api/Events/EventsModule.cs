using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Events;

/// <summary>
/// The Events sub-app's API (SRS "Ndeipi.Modules.Events", sprint 1). The app is a Razor Class
/// Library the shell loads on first use, calling these with the shell's sign-in.
/// </summary>
public static class EventsModule
{
    public static IServiceCollection AddEvents(this IServiceCollection services)
    {
        services.AddScoped<EventsService>();
        services.AddScoped<TicketIssuer>();
        services.AddScoped<IBankTransferListener, TicketPaymentListener>();
        services.AddScoped<ITopicPolicy, EventTopicPolicy>();
        services.AddHostedService<TicketHoldSweeper>();
        return services;
    }

    public static void MapEvents(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + EventsContract.BasePath).RequireAuthorization().RequireSubApp(EventsContract.AppId);

        // ---- Catalog ----

        api.MapGet("", async (string? city, string? category, int? skip, int? take, HttpContext http, EventsService events) =>
            Results.Ok(await events.ListAsync(city, category, skip ?? 0, take ?? 20, http.RequestAborted)));

        api.MapGet("/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.GetAsync(me, id, http.RequestAborted) is { } e ? Results.Ok(e) : Results.NotFound();
        });

        // ---- Organizers ----

        api.MapGet("/mine", async (HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await events.MineAsync(me, http.RequestAborted));
        });

        api.MapPost("", async (SaveEventRequest request, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await events.CreateAsync(me, request, http.RequestAborted));
        });

        api.MapPut("/{id:guid}", async (Guid id, SaveEventRequest request, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.UpdateAsync(me, id, request, http.RequestAborted) is { } e ? Results.Ok(e) : Results.NotFound();
        });

        api.MapPost("/{id:guid}/publish", async (Guid id, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.SetPublishedAsync(me, id, true, http.RequestAborted) is { } e ? Results.Ok(e) : Results.NotFound();
        });

        api.MapPost("/{id:guid}/unpublish", async (Guid id, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.SetPublishedAsync(me, id, false, http.RequestAborted) is { } e ? Results.Ok(e) : Results.NotFound();
        });

        api.MapGet("/{id:guid}/validators", async (Guid id, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.ValidatorsAsync(me, id, http.RequestAborted) is { } v ? Results.Ok(v) : Results.NotFound();
        });

        api.MapPost("/{id:guid}/validators", async (Guid id, AddValidatorRequest request, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.AddValidatorAsync(me, id, request.Email, http.RequestAborted) is { } v ? Results.Ok(v) : Results.NotFound();
        });

        api.MapDelete("/{id:guid}/validators/{userId:guid}", async (Guid id, Guid userId, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.RemoveValidatorAsync(me, id, userId, http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });

        // ---- Buying ----

        api.MapPost("/{id:guid}/holds", async (Guid id, HoldRequest request, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await events.HoldAsync(me, id, request, http.RequestAborted));
        });

        api.MapDelete("/holds/{holdId:guid}", async (Guid holdId, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.ReleaseAsync(me, holdId, http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });

        api.MapPost("/holds/{holdId:guid}/pay", async (Guid holdId, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.PayAsync(me, holdId, http.RequestAborted) is { } o ? Results.Ok(o) : Results.NotFound();
        });

        api.MapGet("/orders/{orderId:guid}", async (Guid orderId, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.OrderAsync(me, orderId, http.RequestAborted) is { } o ? Results.Ok(o) : Results.NotFound();
        });

        // ---- Tickets ----

        api.MapGet("/tickets", async (HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await events.TicketsAsync(me, http.RequestAborted));
        });

        api.MapPost("/tickets/{ticketId:guid}/holder-key", async (Guid ticketId, BindHolderKeyRequest request, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.BindHolderKeyAsync(me, ticketId, request.PublicKeySpki, http.RequestAborted) ? Results.NoContent() : Results.NotFound();
        });

        // ---- Gate ----

        api.MapGet("/{id:guid}/gate", async (Guid id, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.GateListAsync(me, id, http.RequestAborted) is { } g ? Results.Ok(g) : Results.NotFound();
        });

        api.MapPost("/{id:guid}/admissions", async (Guid id, AdmissionsRequest request, HttpContext http, CurrentUserService users, EventsService events) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await events.AdmitAsync(me, id, request, http.RequestAborted) is { } r ? Results.Ok(r) : Results.NotFound();
        });
    }
}
