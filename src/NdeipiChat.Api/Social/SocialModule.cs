using Microsoft.AspNetCore.Mvc;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Social;

public static class SocialModule
{
    /// <summary>Four photos at the per-photo limit, plus the form around them.</summary>
    const long MaxRequestBytes = SocialContract.MaxPhotos * SocialContract.MaxPhotoBytes + 1024 * 1024;

    public static IServiceCollection AddSocial(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<SocialOptions>(config.GetSection(SocialOptions.Section));
        services.AddSingleton<PostMediaStore>();
        services.AddScoped<PostService>();
        return services;
    }

    public static void MapSocial(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(SocialContract.PostsPath).RequireAuthorization();

        api.MapGet("", async (Guid? before, Guid? author, HttpContext http, CurrentUserService users, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await posts.FeedAsync(me.Id, before, author, posts.SiteFor(http.Request), http.RequestAborted));
        });

        // multipart/form-data: "caption", "mint" ("true" to mint straight away) and one "photos" part per photo.
        api.MapPost("", async (HttpContext http, CurrentUserService users, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            if (!http.Request.HasFormContentType)
                throw new ChatRejectedException("Send the post as multipart/form-data.");
            var form = await http.Request.ReadFormAsync(http.RequestAborted);

            var photos = new List<UploadedPhoto>();
            foreach (var file in form.Files.GetFiles("photos"))
            {
                if (file.Length > SocialContract.MaxPhotoBytes)
                    throw new ChatRejectedException($"{file.FileName} is larger than {SocialContract.MaxPhotoBytes / (1024 * 1024)} MB.");
                using var buffer = new MemoryStream();
                await file.CopyToAsync(buffer, http.RequestAborted);
                photos.Add(new UploadedPhoto(file.FileName, buffer.ToArray()));
            }
            var mint = bool.TryParse(form["mint"], out var m) && m;
            return Results.Ok(await posts.CreateAsync(me, form["caption"], photos, mint, posts.SiteFor(http.Request), http.RequestAborted));
        }).WithMetadata(new RequestSizeLimitAttribute(MaxRequestBytes));

        api.MapGet("/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return await posts.GetAsync(me.Id, id, posts.SiteFor(http.Request), http.RequestAborted) is { } post ? Results.Ok(post) : Results.NotFound();
        });

        api.MapDelete("/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            await posts.DeleteAsync(me, id, http.RequestAborted);
            return Results.NoContent();
        });

        api.MapPost("/{id:guid}/like", async (Guid id, HttpContext http, CurrentUserService users, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await posts.SetLikeAsync(me.Id, id, like: true, http.RequestAborted));
        });

        api.MapDelete("/{id:guid}/like", async (Guid id, HttpContext http, CurrentUserService users, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await posts.SetLikeAsync(me.Id, id, like: false, http.RequestAborted));
        });

        api.MapPost("/{id:guid}/mint", async (Guid id, HttpContext http, CurrentUserService users, PostService posts) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return Results.Ok(await posts.MintAsync(me, id, posts.SiteFor(http.Request), http.RequestAborted));
        });

        // Public, no sign-in: an NFT's image and metadata must load in any wallet or marketplace.
        // Photos never change once posted, so they cache for good.
        app.MapGet("/media/posts/{mediaId:guid}/{size}", (Guid mediaId, string size, HttpContext http, PostMediaStore store) =>
        {
            if (store.PathFor(mediaId, size) is not { } path)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "image/jpeg");
        });

        app.MapGet("/nft/posts/{postId:guid}", async (Guid postId, HttpContext http, PostService posts) =>
        {
            if (await posts.MetadataAsync(postId, posts.SiteFor(http.Request), http.RequestAborted) is not { } metadata)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "public, max-age=300";
            return Results.Json(metadata);
        });
    }
}
