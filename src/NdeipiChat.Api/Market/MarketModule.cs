using Microsoft.AspNetCore.Mvc;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Market;

/// <summary>
/// Photos and videos in chats, forwarding, and the livestock Market: listings and offers as chat
/// messages, gathered by the Market sub-app (SRS "Livestock Networking, Marketplace, and Financial
/// Micro App", phase 1).
/// </summary>
public static class MarketModule
{
    public static IServiceCollection AddMarket(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<ChatMediaOptions>(configuration.GetSection(ChatMediaOptions.Section));
        services.AddScoped<ChatMediaService>();
        services.AddScoped<ForwardService>();
        services.AddScoped<MarketService>();
        services.AddMessageKind<MediaMessageHandler>();
        services.AddMessageKind<ListingMessageHandler>();
        services.AddMessageKind<OfferMessageHandler>();
        return services;
    }

    public static void MapMarket(this IEndpointRouteBuilder app)
    {
        var chat = app.MapGroup("/api").RequireAuthorization();

        // A photo or video for a chat, sent next in a "media" message (or a listing) by its id.
        chat.MapPost("/conversations/{id:guid}/media", async (Guid id, HttpContext http, CurrentUserService users, ChatMediaService media) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (form.Files.GetFile("file") is not { } file)
                throw new ChatRejectedException("Choose a photo or video.");
            return Results.Ok(await media.UploadAsync(await users.GetAsync(http.User, http.RequestAborted), id, file, http.RequestAborted));
        }).DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(MarketContract.MaxVideoBytes + 1024 * 1024));

        chat.MapPost("/messages/{id:guid}/forward", async (Guid id, ForwardRequest request, HttpContext http, CurrentUserService users, ForwardService forward) =>
            Results.Ok(await forward.ForwardAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        // Public by unguessable id, as Social's photos are: <img> and <video> can't send a sign-in
        // token. They never change, so they cache for good.
        app.MapGet("/media/chat/{id:guid}/{variant}", (Guid id, string variant, HttpContext http, ChatMediaService media) =>
        {
            if (media.Find(id, variant) is not { } file)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(file.Path, file.ContentType, enableRangeProcessing: true);
        });

        var market = app.MapGroup("/" + MarketContract.BasePath).RequireAuthorization().RequireSubApp(MarketContract.AppId);

        market.MapGet("/listings", async (string? species, string? q, string? tag, string? status, DateTimeOffset? before,
            HttpContext http, CurrentUserService users, MarketService service) =>
            Results.Ok(await service.FeedAsync(await users.GetAsync(http.User, http.RequestAborted), species, q, tag, status, before, http.RequestAborted)));

        market.MapGet("/listings/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, MarketService service) =>
            Found(await service.GetAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        market.MapGet("/listings/{id:guid}/offers", async (Guid id, HttpContext http, CurrentUserService users, MarketService service) =>
            Found(await service.OffersAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        market.MapPut("/listings/{id:guid}/status", async (Guid id, SetListingStatusRequest request, HttpContext http, CurrentUserService users, MarketService service) =>
            Found(await service.SetStatusAsync(await users.GetAsync(http.User, http.RequestAborted), id, request.Status, http.RequestAborted)));

        market.MapPost("/offers/{id:guid}/accept", async (Guid id, HttpContext http, CurrentUserService users, MarketService service) =>
            Found(await service.DecideAsync(await users.GetAsync(http.User, http.RequestAborted), id, accept: true, http.RequestAborted)));

        market.MapPost("/offers/{id:guid}/decline", async (Guid id, HttpContext http, CurrentUserService users, MarketService service) =>
            Found(await service.DecideAsync(await users.GetAsync(http.User, http.RequestAborted), id, accept: false, http.RequestAborted)));

        market.MapPost("/offers/{id:guid}/withdraw", async (Guid id, HttpContext http, CurrentUserService users, MarketService service) =>
            Found(await service.WithdrawAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        market.MapGet("/me", async (HttpContext http, CurrentUserService users, MarketService service) =>
            Results.Ok(await service.MeAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        market.MapGet("/chats", async (HttpContext http, CurrentUserService users, MarketService service) =>
            Results.Ok(await service.ChatsAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        market.MapGet("/tags", async (HttpContext http, CurrentUserService users, MarketService service) =>
            Results.Ok(await service.TagsAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));
    }

    static IResult Found<T>(T? value) where T : class => value is null ? Results.NotFound() : Results.Ok(value);
}
