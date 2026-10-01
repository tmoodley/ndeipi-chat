using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Finance;

/// <summary>The Finance sub-app's API, behind the launcher's access check like every sub-app's.</summary>
public static class FinanceModule
{
    public static IServiceCollection AddFinance(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<FinanceOptions>(configuration.GetSection(FinanceOptions.Section));
        services.AddScoped<FinanceService>();
        services.AddScoped<ITopicPolicy, FinanceTopicPolicy>();
        return services;
    }

    public static void MapFinance(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + FinanceContract.BasePath).RequireAuthorization().RequireSubApp(FinanceContract.AppId);

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, FinanceService finance) =>
            Results.Ok(await finance.MeAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        // ---- Applying ----

        api.MapGet("/applications", async (HttpContext http, CurrentUserService users, FinanceService finance) =>
            Results.Ok(await finance.MineAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapPost("/applications", async (SaveLoanApplicationRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Results.Ok(await finance.CreateAsync(await users.GetAsync(http.User, http.RequestAborted), request, http.RequestAborted)));

        api.MapGet("/applications/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.GetAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapPut("/applications/{id:guid}", async (Guid id, SaveLoanApplicationRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.SaveAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapDelete("/applications/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, FinanceService finance) =>
            await finance.DeleteDraftAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/applications/{id:guid}/documents/{kind}", async (Guid id, string kind, HttpContext http, CurrentUserService users, FinanceService finance) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            if (form.Files.GetFile("file") is not { } file)
                throw new ChatRejectedException("Choose a file to upload.");
            return Found(await finance.UploadAsync(await users.GetAsync(http.User, http.RequestAborted), id, kind, file, http.RequestAborted));
        }).DisableAntiforgery();

        api.MapPost("/applications/{id:guid}/documents/{kind}/link", async (Guid id, string kind, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.DocumentLinkAsync(await users.GetAsync(http.User, http.RequestAborted), id, kind, SiteFor(http.Request), http.RequestAborted)));

        api.MapPost("/applications/{id:guid}/submit", async (Guid id, SubmitLoanRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.SubmitAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapGet("/equipment", async (bool? all, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Results.Ok(await finance.EquipmentAsync(await users.GetAsync(http.User, http.RequestAborted), all == true, http.RequestAborted)));

        api.MapGet("/constituencies", async (string province, HttpContext http, FinanceService finance) =>
            Results.Ok(await finance.ConstituenciesAsync(province, http.RequestAborted)));

        api.MapGet("/route", async (string? province, string? constituency, string? ward, string? village, HttpContext http, FinanceService finance) =>
            Results.Ok(await finance.RouteAsync(province, constituency, ward, village, http.RequestAborted)));

        // ---- Committees ----

        api.MapGet("/reviews", async (HttpContext http, CurrentUserService users, FinanceService finance) =>
            Results.Ok(await finance.QueueAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapPost("/applications/{id:guid}/decisions", async (Guid id, LoanDecisionRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.DecideAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        // ---- Admin ----

        api.MapGet("/committees", async (HttpContext http, CurrentUserService users, FinanceService finance) =>
            Results.Ok(await finance.CommitteesAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapPost("/committees", async (SaveCommitteeRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.SaveCommitteeAsync(await users.GetAsync(http.User, http.RequestAborted), null, request, http.RequestAborted)));

        api.MapPut("/committees/{id:guid}", async (Guid id, SaveCommitteeRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.SaveCommitteeAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapDelete("/committees/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, FinanceService finance) =>
            await finance.DeleteCommitteeAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/equipment", async (SaveEquipmentRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.SaveEquipmentAsync(await users.GetAsync(http.User, http.RequestAborted), null, request, http.RequestAborted)));

        api.MapPut("/equipment/{id:guid}", async (Guid id, SaveEquipmentRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Found(await finance.SaveEquipmentAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapPut("/constituencies", async (SaveConstituenciesRequest request, HttpContext http, CurrentUserService users, FinanceService finance) =>
            Results.Ok(await finance.SaveConstituenciesAsync(await users.GetAsync(http.User, http.RequestAborted), request, http.RequestAborted)));

        // A document link, opened in a new tab without the sign-in token: the token in the path is the permission.
        app.MapGet("/" + FinanceContract.BasePath + "/files/{token}", async (string token, HttpContext http, FinanceService finance) =>
            await finance.OpenLinkAsync(token, http.RequestAborted) is { } file
                ? Results.File(file.Path, file.ContentType, file.FileName, enableRangeProcessing: true)
                : Results.NotFound()).AllowAnonymous();
    }

    static Uri SiteFor(HttpRequest request) => new($"{request.Scheme}://{request.Host}{request.PathBase}/");

    static IResult Found<T>(T? value) where T : class => value is null ? Results.NotFound() : Results.Ok(value);
}
