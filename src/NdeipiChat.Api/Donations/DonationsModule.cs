using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Donations;

/// <summary>The Donations sub-app's API, behind the launcher's access check like every sub-app's.</summary>
public static class DonationsModule
{
    public static IServiceCollection AddDonations(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<DonationsOptions>(configuration.GetSection(DonationsOptions.Section));
        services.AddScoped<DonationsService>();
        services.AddScoped<DonationSettlement>();
        services.AddScoped<IBankTransferListener, DonationPaymentListener>();
        services.AddScoped<ITopicPolicy, DonationTopicPolicy>();
        return services;
    }

    public static void MapDonations(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + DonationsContract.BasePath).RequireAuthorization().RequireSubApp(DonationsContract.AppId);

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, DonationsService donations) =>
            Results.Ok(await donations.MeAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        // ---- Campaigns (FR-02) ----

        api.MapGet("/campaigns", async (int? skip, int? take, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Results.Ok(await donations.FeedAsync(await users.GetAsync(http.User, http.RequestAborted), skip ?? 0, take ?? 20, http.RequestAborted)));

        api.MapGet("/campaigns/mine", async (HttpContext http, CurrentUserService users, DonationsService donations) =>
            Results.Ok(await donations.MineAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapGet("/campaigns/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Found(await donations.GetAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapPost("/campaigns", async (SaveCampaignRequest request, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Results.Ok(await donations.CreateAsync(await users.GetAsync(http.User, http.RequestAborted), request, http.RequestAborted)));

        api.MapPut("/campaigns/{id:guid}", async (Guid id, SaveCampaignRequest request, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Found(await donations.UpdateAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapPost("/campaigns/{id:guid}/publish", async (Guid id, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Found(await donations.SetOpenAsync(await users.GetAsync(http.User, http.RequestAborted), id, open: true, http.RequestAborted)));

        api.MapPost("/campaigns/{id:guid}/close", async (Guid id, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Found(await donations.SetOpenAsync(await users.GetAsync(http.User, http.RequestAborted), id, open: false, http.RequestAborted)));

        api.MapPut("/campaigns/{id:guid}/verified", async (Guid id, bool verified, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Found(await donations.VerifyAsync(await users.GetAsync(http.User, http.RequestAborted), id, verified, http.RequestAborted)));

        // ---- Giving (FR-01) and receipts (FR-04) ----

        api.MapPost("/campaigns/{id:guid}/donations", async (Guid id, DonateRequest request, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Found(await donations.DonateAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapGet("/donations", async (HttpContext http, CurrentUserService users, DonationsService donations) =>
            Results.Ok(await donations.MyDonationsAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapGet("/donations/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, DonationsService donations) =>
            Found(await donations.DonationAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapGet("/receipts/{hash}", async (string hash, HttpContext http, DonationsService donations) =>
            Results.Ok(await donations.CheckReceiptAsync(hash, http.RequestAborted)));
    }

    static IResult Found<T>(T? value) where T : class => value is null ? Results.NotFound() : Results.Ok(value);
}
