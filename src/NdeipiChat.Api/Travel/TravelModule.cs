using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Banking;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Launcher;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Travel;

public sealed record PublishRequest(bool Published);

public sealed record ReasonRequest(string? Reason);

/// <summary>Lets Travel's API (Hotel &amp; Tours Booking SRS), behind the launcher's sub-app check like every sub-app's.</summary>
public static class TravelModule
{
    public static IServiceCollection AddTravel(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TravelOptions>(configuration.GetSection(TravelOptions.Section));
        services.AddScoped<TravelPhotos>();
        services.AddScoped<TravelCatalog>();
        services.AddScoped<TravelIssuer>();
        services.AddScoped<TravelBookings>();
        services.AddScoped<TravelInvoices>();
        services.AddScoped<IBankTransferListener, TravelPaymentListener>();
        services.AddScoped<ITopicPolicy, TravelTopicPolicy>();
        services.AddHostedService<TravelHoldSweeper>();
        return services;
    }

    public static void MapTravel(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/" + TravelContract.BasePath).RequireAuthorization().RequireSubApp(TravelContract.AppId);

        api.MapGet("/me", async (HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.MeAsync(await Me(http, users), http.RequestAborted)));

        // ---- Finding (FR-1)
        api.MapPost("/search", async (TravelSearchRequest request, HttpContext http, TravelCatalog catalog) =>
            Results.Ok(await catalog.SearchAsync(request, http.RequestAborted)));
        api.MapGet("/destinations", async (int? count, HttpContext http, TravelCatalog catalog) =>
            Results.Ok(await catalog.DestinationsAsync(count ?? 6, http.RequestAborted)));
        api.MapGet("/properties/{id:guid}", async (Guid id, DateOnly? checkIn, DateOnly? checkOut, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Found(await catalog.PropertyAsync(await Me(http, users), id, checkIn, checkOut, http.RequestAborted)));
        api.MapGet("/tours/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Found(await catalog.TourAsync(await Me(http, users), id, http.RequestAborted)));

        // ---- Booking (FR-2.3, FR-4, FR-5)
        api.MapPost("/holds", async (TravelHoldRequest request, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Results.Ok(await bookings.HoldAsync(await Me(http, users), request, http.RequestAborted)));
        api.MapGet("/holds/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Found(await bookings.GetHoldAsync(await Me(http, users), id, http.RequestAborted)));
        api.MapDelete("/holds/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            await bookings.ReleaseAsync(await Me(http, users), id, http.RequestAborted) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/holds/{id:guid}/checkout", async (Guid id, TravelCheckoutRequest request, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Results.Ok(await bookings.CheckoutAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapGet("/bookings", async (HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Results.Ok(await bookings.MineAsync(await Me(http, users), http.RequestAborted)));
        api.MapGet("/bookings/{id:guid}", async (Guid id, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Found(await bookings.GetAsync(await Me(http, users), id, http.RequestAborted)));
        api.MapPut("/bookings/{id:guid}/key", async (Guid id, BindTravelKeyRequest request, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            await bookings.BindKeyAsync(await Me(http, users), id, request.PublicKey, http.RequestAborted) ? Results.NoContent() : Results.NotFound());
        api.MapGet("/bookings/{id:guid}/invoice", async (Guid id, HttpContext http, CurrentUserService users, TravelInvoices invoices) =>
            Found(await invoices.InvoiceAsync(await Me(http, users), id, http.RequestAborted)));
        api.MapGet("/bookings/{id:guid}/voucher.pdf", async (Guid id, HttpContext http, CurrentUserService users, TravelInvoices invoices) =>
            await invoices.VoucherPdfAsync(await Me(http, users), id, http.RequestAborted) is { } pdf
                ? Results.File(pdf.Pdf, "application/pdf", pdf.Name)
                : Results.NotFound());

        // ---- Vendor portal
        api.MapPut("/vendor", async (SaveVendorRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SaveVendorAsync(await Me(http, users), request, http.RequestAborted)));
        api.MapGet("/vendor/dashboard", async (HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Results.Ok(await bookings.DashboardAsync(await Me(http, users), http.RequestAborted)));
        api.MapPost("/vendor/properties", async (SavePropertyRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SavePropertyAsync(await Me(http, users), null, request, http.RequestAborted)));
        api.MapPut("/vendor/properties/{id:guid}", async (Guid id, SavePropertyRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SavePropertyAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/vendor/properties/{id:guid}/rooms", async (Guid id, SaveRoomTypeRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SaveRoomTypeAsync(await Me(http, users), id, null, request, http.RequestAborted)));
        api.MapPut("/vendor/properties/{id:guid}/rooms/{roomId:guid}", async (Guid id, Guid roomId, SaveRoomTypeRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SaveRoomTypeAsync(await Me(http, users), id, roomId, request, http.RequestAborted)));
        api.MapDelete("/vendor/properties/{id:guid}/rooms/{roomId:guid}", async (Guid id, Guid roomId, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
        {
            await catalog.ArchiveRoomTypeAsync(await Me(http, users), id, roomId, http.RequestAborted);
            return Results.NoContent();
        });
        api.MapGet("/vendor/properties/{id:guid}/rooms/{roomId:guid}/calendar", async (Guid id, Guid roomId, DateOnly from, int? days, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.CalendarAsync(await Me(http, users), id, roomId, from, days ?? 31, http.RequestAborted)));
        api.MapPut("/vendor/properties/{id:guid}/rooms/{roomId:guid}/rates", async (Guid id, Guid roomId, SetRatesRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
        {
            await catalog.SetRatesAsync(await Me(http, users), id, roomId, request, http.RequestAborted);
            return Results.NoContent();
        });
        api.MapPost("/vendor/tours", async (SaveTourRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SaveTourAsync(await Me(http, users), null, request, http.RequestAborted)));
        api.MapPut("/vendor/tours/{id:guid}", async (Guid id, SaveTourRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SaveTourAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/vendor/tours/{id:guid}/addons", async (Guid id, SaveAddOnRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SaveAddOnAsync(await Me(http, users), id, null, request, http.RequestAborted)));
        api.MapPut("/vendor/tours/{id:guid}/addons/{addOnId:guid}", async (Guid id, Guid addOnId, SaveAddOnRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.SaveAddOnAsync(await Me(http, users), id, addOnId, request, http.RequestAborted)));
        api.MapDelete("/vendor/tours/{id:guid}/addons/{addOnId:guid}", async (Guid id, Guid addOnId, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
        {
            await catalog.ArchiveAddOnAsync(await Me(http, users), id, addOnId, http.RequestAborted);
            return Results.NoContent();
        });
        api.MapPost("/vendor/tours/{id:guid}/departures", async (Guid id, SaveDepartureRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.AddDepartureAsync(await Me(http, users), id, request, http.RequestAborted)));
        api.MapPost("/vendor/tours/{id:guid}/departures/{departureId:guid}/reschedule", async (Guid id, Guid departureId, RescheduleRequest request, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Results.Ok(await bookings.RescheduleAsync(await Me(http, users), id, departureId, request, http.RequestAborted)));
        api.MapPost("/vendor/{type}/{id:guid}/photos", async (string type, Guid id, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
        {
            var form = await http.Request.ReadFormAsync(http.RequestAborted);
            var file = form.Files.GetFile("file") ?? throw new ChatRejectedException("Choose a photo to upload.");
            return Results.Ok(await catalog.AddPhotoAsync(await Me(http, users), ListingType(type), id, file, http.RequestAborted));
        }).DisableAntiforgery();
        api.MapDelete("/vendor/{type}/{id:guid}/photos/{photoId:guid}", async (string type, Guid id, Guid photoId, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
            Results.Ok(await catalog.RemovePhotoAsync(await Me(http, users), ListingType(type), id, photoId, http.RequestAborted)));
        api.MapPost("/vendor/{type}/{id:guid}/publish", async (string type, Guid id, PublishRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
        {
            await catalog.PublishAsync(await Me(http, users), ListingType(type), id, request.Published, http.RequestAborted);
            return Results.NoContent();
        });
        api.MapPost("/vendor/bookings/{id:guid}/cancel", async (Guid id, ReasonRequest request, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Results.Ok(await bookings.CancelByVendorAsync(await Me(http, users), id, request.Reason, http.RequestAborted)));
        api.MapPost("/vendor/check-in", async (CheckInRequest request, HttpContext http, CurrentUserService users, TravelBookings bookings) =>
            Results.Ok(await bookings.CheckInAsync(await Me(http, users), request.Code, http.RequestAborted)));

        // ---- Admins (NFR-6)
        api.MapPut("/admin/{type}/{id:guid}/status", async (string type, Guid id, SetStatusRequest request, HttpContext http, CurrentUserService users, TravelCatalog catalog) =>
        {
            await catalog.AdminStatusAsync(await Me(http, users), type, id, request, http.RequestAborted);
            return Results.NoContent();
        });

        // An expiring link to a listing photo: the token is the permission, so <img> needs no sign-in token.
        app.MapGet("/" + TravelContract.BasePath + "/photos/{token}", (string token, HttpContext http, TravelPhotos photos) =>
        {
            if (photos.Open(token) is not { } path)
                return Results.NotFound();
            http.Response.Headers.CacheControl = "private, max-age=3600";
            return Results.File(path, "image/jpeg");
        }).AllowAnonymous();

        // FR-4.4: anyone holding an invoice can fetch the public key to check its signature.
        app.MapGet("/" + TravelContract.BasePath + "/invoice-keys/{keyId}", async (string keyId, TravelInvoices invoices, HttpContext http) =>
            await invoices.PublicKeyAsync(keyId, http.RequestAborted) is { } key
                ? Results.Ok(new { key_id = keyId, algorithm = "ECDSA P-256 SHA-256 (IEEE P1363 signature)", public_key_spki = key })
                : Results.NotFound()).AllowAnonymous();
    }

    static string ListingType(string type) => type switch
    {
        "properties" or TravelItemTypes.Stay => TravelItemTypes.Stay,
        "tours" or TravelItemTypes.Tour => TravelItemTypes.Tour,
        _ => throw new ChatRejectedException("That isn't a kind of listing.")
    };

    static Task<User> Me(HttpContext http, CurrentUserService users) => users.GetAsync(http.User, http.RequestAborted);

    static IResult Found<T>(T? value) where T : class => value is null ? Results.NotFound() : Results.Ok(value);
}
