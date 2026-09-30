using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Pos;

/// <summary>
/// The POS sub-app's API. Back-office calls act as the signed-in person; till calls also carry the
/// till session (<see cref="PosContract.TillHeader"/>) of whoever unlocked it with their PIN.
/// </summary>
public static class PosModule
{
    public static IServiceCollection AddPos(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<PosService>();
        services.AddScoped<PosPayService>();
        services.AddScoped<IBankTransferListener, PosQrPaymentListener>();
        services.AddMessageKind<PosReceiptHandler>();
        return services;
    }

    /// <summary>The Ndeipi site, for the pay links in QR codes: the one the till was loaded from.</summary>
    static Uri SiteOf(HttpContext http) => new($"{http.Request.Scheme}://{http.Request.Host}{http.Request.PathBase}/");

    public static void MapPos(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + PosContract.BasePath).RequireAuthorization().RequireSubApp(PosContract.AppId);

        // ---- Merchants, stores and staff ----

        api.MapGet("/merchants", async (HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.MerchantsAsync(await users.GetAsync(http.User, http.RequestAborted), http.RequestAborted)));

        api.MapPost("/merchants", async (CreateMerchantRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.CreateMerchantAsync(await users.GetAsync(http.User, http.RequestAborted), request, http.RequestAborted)));

        api.MapGet("/merchants/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.MerchantAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapPut("/merchants/{id:guid}", async (Guid id, SaveMerchantRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.SaveMerchantAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapPost("/merchants/{id:guid}/stores", async (Guid id, SaveStoreRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.AddStoreAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapPut("/stores/{id:guid}", async (Guid id, SaveStoreRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.SaveStoreAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapGet("/merchants/{id:guid}/staff", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.StaffAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapPost("/merchants/{id:guid}/staff", async (Guid id, AddStaffRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.AddStaffAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapPut("/staff/{id:guid}", async (Guid id, SaveStaffRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.SaveStaffAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapDelete("/staff/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            await pos.RemoveStaffAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) ? Results.NoContent() : Results.NotFound());

        api.MapPut("/merchants/{id:guid}/pin", async (Guid id, SetPinRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
        {
            await pos.SetPinAsync(await users.GetAsync(http.User, http.RequestAborted), id, request.Pin, http.RequestAborted);
            return Results.NoContent();
        });

        // ---- Catalogue and stock ----

        api.MapGet("/merchants/{id:guid}/categories", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.CategoriesAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapPost("/merchants/{id:guid}/categories", async (Guid id, SaveCategoryRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.SaveCategoryAsync(await users.GetAsync(http.User, http.RequestAborted), id, null, request, http.RequestAborted)));

        api.MapPut("/merchants/{id:guid}/categories/{categoryId:guid}", async (Guid id, Guid categoryId, SaveCategoryRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.SaveCategoryAsync(await users.GetAsync(http.User, http.RequestAborted), id, categoryId, request, http.RequestAborted)));

        api.MapDelete("/categories/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            await pos.DeleteCategoryAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted) ? Results.NoContent() : Results.NotFound());

        api.MapPost("/merchants/{id:guid}/products", async (Guid id, SaveProductRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.SaveProductAsync(await users.GetAsync(http.User, http.RequestAborted), id, null, request, http.RequestAborted)));

        api.MapPut("/merchants/{id:guid}/products/{productId:guid}", async (Guid id, Guid productId, SaveProductRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.SaveProductAsync(await users.GetAsync(http.User, http.RequestAborted), id, productId, request, http.RequestAborted)));

        api.MapGet("/stores/{id:guid}/catalog", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.CatalogAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapGet("/stores/{id:guid}/lookup", async (Guid id, string? code, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.LookupAsync(await users.GetAsync(http.User, http.RequestAborted), id, code, http.RequestAborted)));

        api.MapPost("/stores/{id:guid}/stock", async (Guid id, StockAdjustRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            Results.Ok(await pos.AdjustStockAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted)));

        api.MapGet("/stores/{id:guid}/alerts", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.StockAlertsAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        // ---- Managers and auditors ----

        api.MapGet("/stores/{id:guid}/dashboard", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.DashboardAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapGet("/stores/{id:guid}/sales", async (Guid id, DateOnly? day, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.SalesAsync(await users.GetAsync(http.User, http.RequestAborted), id, day, http.RequestAborted)));

        api.MapGet("/sales/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.SaleAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        api.MapGet("/merchants/{id:guid}/audit", async (Guid id, long? before, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.AuditTrailAsync(await users.GetAsync(http.User, http.RequestAborted), id, before, http.RequestAborted)));

        api.MapGet("/merchants/{id:guid}/audit/verify", async (Guid id, HttpContext http, CurrentUserService users, PosService pos) =>
            Found(await pos.VerifyAuditAsync(await users.GetAsync(http.User, http.RequestAborted), id, http.RequestAborted)));

        // ---- The till ----

        api.MapPost("/stores/{id:guid}/till", async (Guid id, UnlockTillRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
        {
            var (session, _) = await pos.UnlockAsync(await users.GetAsync(http.User, http.RequestAborted), id, request, http.RequestAborted);
            return Results.Ok(session);
        });

        var till = api.MapGroup("/till");

        till.MapGet("", async (HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Results.Ok(await pos.SessionAsync(t, TokenOf(http)!, http.RequestAborted))));

        till.MapDelete("", async (HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t =>
            {
                await pos.LockAsync(t, http.RequestAborted);
                return Results.NoContent();
            }));

        till.MapPost("/shift", async (OpenShiftRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Results.Ok(await pos.OpenShiftAsync(t, request.OpeningFloat, http.RequestAborted))));

        till.MapGet("/shift", async (HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => await pos.CurrentShiftAsync(t, http.RequestAborted) is { } s ? Results.Ok(s) : Results.NoContent()));

        till.MapPost("/shift/cash", async (CashMovementRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Results.Ok(await pos.MoveCashAsync(t, request, http.RequestAborted))));

        till.MapPost("/shift/close", async (CloseShiftRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Results.Ok(await pos.CloseShiftAsync(t, request.CountedCash, http.RequestAborted))));

        till.MapPost("/quote", async (CheckoutRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Results.Ok(await pos.QuoteAsync(t, request, http.RequestAborted))));

        // An offline sale may come in under a till session that has locked since it was rung up.
        till.MapPost("/sales", async (CheckoutRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Results.Ok(await pos.CheckoutAsync(t, request, http.RequestAborted)),
                request.Offline ? request.OccurredAt : null));

        till.MapPost("/sales/{id:guid}/void", async (Guid id, ReverseSaleRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Found(await pos.ReverseAsync(t, id, refund: false, request, http.RequestAborted))));

        till.MapPost("/sales/{id:guid}/refund", async (Guid id, ReverseSaleRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
            await WithTillAsync(http, users, pos, async t => Found(await pos.ReverseAsync(t, id, refund: true, request, http.RequestAborted))));

        till.MapPost("/sales/{id:guid}/receipt", async (Guid id, SendReceiptRequest request, HttpContext http, CurrentUserService users, PosService pos) =>
        {
            var device = await users.GetAsync(http.User, http.RequestAborted);
            return await WithTillAsync(http, users, pos, async t =>
                await pos.SendReceiptAsync(device, t, id, request.EmailOrPhone, http.RequestAborted) ? Results.NoContent() : Results.NotFound());
        });

        // ---- Ndeipi Pay: the till's QR code ----

        till.MapPost("/qr-payments", async (StartQrPaymentRequest request, HttpContext http, CurrentUserService users, PosService pos, PosPayService pay) =>
            await WithTillAsync(http, users, pos, async t => Results.Ok(await pay.StartAsync(t, request, SiteOf(http), http.RequestAborted))));

        till.MapGet("/qr-payments/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PosService pos, PosPayService pay) =>
            await WithTillAsync(http, users, pos, async t => Found(await pay.GetAsync(t, id, SiteOf(http), http.RequestAborted))));

        till.MapDelete("/qr-payments/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, PosService pos, PosPayService pay) =>
            await WithTillAsync(http, users, pos, async t => Found(await pay.CancelAsync(t, id, SiteOf(http), http.RequestAborted))));

        // ---- Ndeipi Pay: the customer who scanned it. Anyone signed in to Ndeipi, not only POS staff. ----

        var customer = app.MapGroup("/" + PosPay.BasePath).RequireAuthorization();

        customer.MapGet("/{code}", async (string code, HttpContext http, CurrentUserService users, PosPayService pay) =>
            Found(await pay.RequestAsync(await users.GetAsync(http.User, http.RequestAborted), code, http.RequestAborted)));

        customer.MapPost("/{code}", async (string code, HttpContext http, CurrentUserService users, PosPayService pay) =>
            Found(await pay.PayAsync(await users.GetAsync(http.User, http.RequestAborted), code, http.RequestAborted)));
    }

    static IResult Found<T>(T? value) where T : class => value is null ? Results.NotFound() : Results.Ok(value);

    static string? TokenOf(HttpContext http) => http.Request.Headers[PosContract.TillHeader].FirstOrDefault();

    /// <summary>Runs a till call if its session is good; otherwise 401 with a reason, so the till asks for a PIN again.</summary>
    static async Task<IResult> WithTillAsync(HttpContext http, CurrentUserService users, PosService pos, Func<Till, Task<IResult>> work, DateTimeOffset? offlineAt = null)
    {
        var device = await users.GetAsync(http.User, http.RequestAborted);
        var till = await pos.TillAsync(device, TokenOf(http), http.RequestAborted, offlineAt);
        return till is null
            ? Results.Problem(title: "The till is locked. Enter your PIN.", statusCode: StatusCodes.Status401Unauthorized)
            : await work(till);
    }
}
