using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Lending;

/// <summary>Ndeipi Lending's API (RWA Lending Platform Portal SRS, Platform &amp; Shared tab), behind the launcher sub-app check. The three portals share it, and their entries require one role, so checking one covers all three.</summary>
public static class LendingModule
{
    public static IServiceCollection AddLending(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LendingOptions>(configuration.GetSection(LendingOptions.Section));
        services.AddScoped<LendingService>();
        return services;
    }

    public static void MapLending(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + LendingContract.BasePath).RequireAuthorization().RequireSubApp(LendingContract.BrokerAppId);

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.MeAsync(await Me(http, users), http.RequestAborted)));
        api.MapGet("/deals", async (HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.DealsAsync(await Me(http, users), http.RequestAborted)));
        api.MapPost("/deals", async (LendingTermsRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.CreateAsync(await Me(http, users), request, http.RequestAborted)));
        api.MapPost("/deals/reference", async (HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.CreateReferenceAsync(await Me(http, users), http.RequestAborted)));
        api.MapGet("/deals/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.DealAsync(await Me(http, users), id, http.RequestAborted)));
        api.MapGet("/deals/{id:guid}/audit", async (Guid id, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.AuditAsync(await Me(http, users), id, http.RequestAborted)));

        api.MapPut("/deals/{id:guid}/terms", async (Guid id, LendingTermsRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.UpdateTermsAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/roles", async (Guid id, LendingAppointRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.AppointAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/transition", async (Guid id, LendingTransitionRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.TransitionAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/deposits", async (Guid id, LendingDepositRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.DepositAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/documents", async (Guid id, LendingDocumentRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.AddDocumentAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/attestations", async (Guid id, LendingAttestationRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.AttestAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/collateral/lock", async (Guid id, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.LockCollateralAsync(await Me(http, users), id, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/draws", async (Guid id, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.DrawAsync(await Me(http, users), id, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/payments", async (Guid id, LendingPaymentRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.PayAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/default-votes", async (Guid id, LendingReasonRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.VoteDefaultAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/write-off", async (Guid id, LendingReasonRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.WriteOffAsync(await Me(http, users), id, request, http.RequestAborted)));

        // ---- The portals
        api.MapGet("/broker/pipeline", async (HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.PipelineAsync(await Me(http, users), http.RequestAborted)));
        api.MapGet("/lender/portfolio", async (HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.PortfolioAsync(await Me(http, users), http.RequestAborted)));
        api.MapGet("/borrower/loans", async (HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.LoansAsync(await Me(http, users), http.RequestAborted)));
        api.MapGet("/notices", async (HttpContext http, CurrentUserService users, LendingService lending) =>
            Results.Ok(await lending.NoticesAsync(await Me(http, users), http.RequestAborted)));
        api.MapPost("/notices/read", async (HttpContext http, CurrentUserService users, LendingService lending) =>
        {
            await lending.MarkNoticesReadAsync(await Me(http, users), http.RequestAborted);
            return Results.NoContent();
        });
        api.MapPost("/deals/{id:guid}/submit", async (Guid id, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.SubmitAsync(await Me(http, users), id, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/return", async (Guid id, LendingReasonRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.ReturnAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/redemptions", async (Guid id, LendingRedeemRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.RequestRedemptionAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/redemptions/{redemptionId:guid}/redeem", async (Guid id, Guid redemptionId, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.RedeemAsync(await Me(http, users), id, redemptionId, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/repay", async (Guid id, LendingRepayRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.RepayAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/deals/{id:guid}/inspection", async (Guid id, LendingInspectionRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
            Found(await lending.RequestInspectionAsync(await Me(http, users), id, request, http.RequestAborted)));

        api.MapPut("/compliance/profiles", async (LendingProfileRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
        {
            await lending.SetProfileAsync(await Me(http, users), request, http.RequestAborted);
            return Results.NoContent();
        });
        api.MapPost("/compliance/top-up", async (LendingTopUpRequest request, HttpContext http, CurrentUserService users, LendingService lending) =>
        {
            await lending.TopUpAsync(await Me(http, users), request, http.RequestAborted);
            return Results.NoContent();
        });
    }

    static Task<User> Me(HttpContext http, CurrentUserService users) => users.GetAsync(http.User, http.RequestAborted);

    static IResult Found<T>(T? value) where T : class => value is null ? Results.NotFound() : Results.Ok(value);
}
