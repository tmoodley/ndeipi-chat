using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Rwa;

/// <summary>
/// Ndeipi RWA's API (RWA &amp; Time-Stripping SRS section 5), behind the launcher's sub-app check like
/// every sub-app's. The buyer is always the signed-in person: the SRS's clerkUserId fields aren't taken
/// from the body or the path.
/// </summary>
public static class RwaModule
{
    public static IServiceCollection AddRwa(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<RwaOptions>(configuration.GetSection(RwaOptions.Section));
        services.AddScoped<RwaService>();
        services.AddHostedService<RwaVestingSweeper>();
        return services;
    }

    public static void MapRwa(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + RwaContract.BasePath).RequireAuthorization().RequireSubApp(RwaContract.AppId);

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, RwaService rwa) =>
            Results.Ok(await rwa.MeAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapGet("/assets", async (HttpContext http, RwaService rwa) =>
            Results.Ok(await rwa.AssetsAsync(http.RequestAborted)));

        api.MapGet("/assets/{code}", async (string code, HttpContext http, RwaService rwa) =>
            await rwa.AssetAsync(code, http.RequestAborted) is { } asset ? Results.Ok(asset) : Results.NotFound());

        // FR-2.1 with FR-1.2 and FR-1.3: KYC, then the money into a cooling-off escrow.
        api.MapPost("/presale/mint", async (RwaMintRequest request, HttpContext http, CurrentUserService users, RwaService rwa) =>
            Results.Ok(await rwa.MintAsync(await users.GetAsync(http.User, http.RequestAborted), request, http.RequestAborted)));

        api.MapPost("/holdings/{id:guid}/cancel", async (Guid id, HttpContext http, CurrentUserService users, RwaService rwa) =>
            await rwa.CancelAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) is { } holding ? Results.Ok(holding) : Results.NotFound());

        api.MapGet("/portfolio", async (HttpContext http, CurrentUserService users, RwaService rwa) =>
            Results.Ok(await rwa.PortfolioAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        // FR-2.4, NFR-2: only an oracle moves a NAV, by a bounded step, with a stated basis.
        api.MapPost("/admin/nav", async (RwaNavUpdateRequest request, HttpContext http, CurrentUserService users, RwaService rwa) =>
        {
            var me = await users.GetAsync(http.User, http.RequestAborted);
            return RwaService.IsOracle(me) ? Results.Ok(await rwa.PostNavAsync(me, request, http.RequestAborted)) : Results.Forbid();
        });
    }
}
