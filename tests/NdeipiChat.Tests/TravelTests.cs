using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Api;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Points;
using NdeipiChat.Api.Travel;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;
using SkiaSharp;

namespace NdeipiChat.Tests;

/// <summary>
/// Lets Travel against the Hotel &amp; Tours Booking SRS: the vendor portal, search and facets (FR-1),
/// holds (FR-2.3), wallet checkout with the fee and points (FR-4), confirmation with the chat and the
/// signed invoice (FR-4.4, FR-5.3), rescheduling (FR-3.3), check-in from the voucher (FR-5.1), and the
/// acceptance criteria: one room for a thousand buyers, and holds released after expiry.
/// </summary>
public sealed class TravelTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = TravelContract.BasePath;

    static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    static DateOnly Today => DateOnly.FromDateTime(DateTime.UtcNow);

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    async Task<TestUser> VerifiedAsync(string name)
    {
        var user = await app.CreateUserAsync(name);
        var (customer, wallet) = (NewId("cust"), NewId("wal"));
        await app.DbAsync(db =>
        {
            db.BankingProfiles.Add(new BankingProfile
            {
                UserId = user.Id, BridgeCustomerId = customer, KycLinkId = NewId("kyc"), KycStatus = BankingProfile.Approved, TosStatus = BankingProfile.Approved,
                WalletId = wallet, WalletChain = "solana", WalletAddress = NewId("addr"), UpdatedAt = DateTimeOffset.UtcNow
            });
            return db.SaveChangesAsync();
        });
        app.Bridge.OnJson(HttpMethod.Get, $"/v0/customers/{customer}/wallets/{wallet}",
            new { id = wallet, chain = "solana", address = "So1ana", balances = new[] { new { balance = "5000.00", currency = "usdc", chain = "solana" } } });
        return user;
    }

    void TransfersSucceed() => app.Bridge.On(HttpMethod.Post, "/v0/transfers", _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { id = NewId("tr"), state = "payment_processed" }), Encoding.UTF8, "application/json")
    });

    void TransfersFail() => app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { code = "insufficient_funds", message = "Insufficient funds" }, HttpStatusCode.BadRequest);

    /// <summary>A vendor with a published lodge (a deluxe room type of <paramref name="units"/>) and a published river cruise.</summary>
    async Task<(TestUser Vendor, PropertyDto Lodge, RoomTypeDto Room, TourDto Cruise, DepartureDto Departure)> VendorAsync(string name, int units = 3, string city = "Victoria Falls")
    {
        var vendor = await VerifiedAsync(name);
        await Put<TravelVendorDto>(vendor, $"{Base}/vendor", new SaveVendorRequest(name, "Family-run since 1998.", "+260 97 000 0000", null));
        var lodge = await vendor.PostAsync<PropertyDto>($"{Base}/vendor/properties", new SavePropertyRequest(
            $"{name} River Lodge", "lodge", "Twelve chalets on the Zambezi, twenty minutes from the Falls.", city, "Zambia", "Plot 9, Riverside Drive",
            -17.85, 25.85, 4, ["wifi", "pool", "game_drives"], "moderate", "14:00", "10:00"));
        var room = await vendor.PostAsync<RoomTypeDto>($"{Base}/vendor/properties/{lodge.Id}/rooms",
            new SaveRoomTypeRequest("Deluxe chalet", "deluxe", "full_board", 2, units, 180m, "River view, private deck."));
        await vendor.PostAsync<RoomTypeDto>($"{Base}/vendor/properties/{lodge.Id}/rooms",
            new SaveRoomTypeRequest("Garden room", "standard", "bed_breakfast", 3, 5, 120m, null));
        (await vendor.Http.PostAsJsonAsync($"{Base}/vendor/stay/{lodge.Id}/publish", new { published = true })).EnsureSuccessStatusCode();

        var cruise = await vendor.PostAsync<TourDto>($"{Base}/vendor/tours", new SaveTourRequest(
            "Zambezi sunset cruise", "river_cruise", "Two and a half hours upriver at sundown.", city, "Zambia", -17.9, 25.84, 2.5, 12, "easy", 65m, true,
            "Drinks and snacks", "Boat club jetty"));
        await vendor.PostAsync<TourAddOnDto>($"{Base}/vendor/tours/{cruise.Id}/addons", new SaveAddOnRequest("National park fee", 10m, true, true));
        await vendor.PostAsync<TourAddOnDto>($"{Base}/vendor/tours/{cruise.Id}/addons", new SaveAddOnRequest("Hotel transfer", 15m, false, false));
        var departure = await vendor.PostAsync<DepartureDto>($"{Base}/vendor/tours/{cruise.Id}/departures",
            new SaveDepartureRequest(DateTimeOffset.UtcNow.AddDays(10).Date.AddHours(15), 4, "Mwila"));
        (await vendor.Http.PostAsJsonAsync($"{Base}/vendor/tour/{cruise.Id}/publish", new { published = true })).EnsureSuccessStatusCode();
        cruise = await vendor.GetAsync<TourDto>($"{Base}/tours/{cruise.Id}");
        return (vendor, lodge, room, cruise, departure);
    }

    static async Task<T> Put<T>(TestUser user, string path, object body)
    {
        using var response = await user.Http.PutAsJsonAsync(path, body, ContractJson.Options);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    async Task<TravelBookingDto> BookStayAsync(TestUser traveler, RoomTypeDto room, DateOnly checkIn, int nights = 2, bool redeem = false)
    {
        var hold = await traveler.PostAsync<TravelHoldDto>($"{Base}/holds", new TravelHoldRequest(TravelItemTypes.Stay, room.Id, checkIn, checkIn.AddDays(nights), 1, 2));
        return await traveler.PostAsync<TravelBookingDto>($"{Base}/holds/{hold.Id}/checkout",
            new TravelCheckoutRequest(traveler.Name, "+263 77 123 4567", "ZW123456", "Late arrival, about 9pm.", redeem));
    }

    [Fact]
    public async Task Only_people_who_passed_KYC_can_sell_and_a_vendor_lists_rooms_and_tours()
    {
        var unverified = await app.CreateUserAsync("Not Verified Lodge");
        using var refused = await unverified.Http.PutAsJsonAsync($"{Base}/vendor", new SaveVendorRequest("Nope Lodge", null, null, null), ContractJson.Options);
        var (vendor, lodge, room, cruise, departure) = await VendorAsync("Mukuni");

        Assert.Contains("Verify your identity", await ProblemAsync(refused));
        Assert.True((await unverified.GetAsync<TravelMeDto>($"{Base}/me")).Vendor is null);
        var me = await vendor.GetAsync<TravelMeDto>($"{Base}/me");
        Assert.Equal("Mukuni", me.Vendor!.Name);
        Assert.Equal(["game_drives", "pool", "wifi"], lodge.Amenities.Order());
        Assert.Equal(("published", 2), (cruise.Status, cruise.AddOns.Count));
        Assert.Equal(4, Assert.Single(cruise.Departures).Available);
        var dashboard = await vendor.GetAsync<VendorDashboardDto>($"{Base}/vendor/dashboard");
        Assert.Equal(2, dashboard.Listings.Count);
    }

    [Fact]
    public async Task Search_finds_stays_by_place_dates_and_party_with_facets_and_the_nightly_rate()
    {
        var city = "Livingstone " + Guid.NewGuid().ToString("N")[..6];
        var (vendor, lodge, room, _, _) = await VendorAsync("Thorntree", city: city);
        var checkIn = Today.AddDays(30);
        // A peak-season price for one of the two nights.
        using (var rates = await vendor.Http.PutAsJsonAsync($"{Base}/vendor/properties/{lodge.Id}/rooms/{room.Id}/rates",
                   new SetRatesRequest(checkIn, checkIn, 220m, null), ContractJson.Options))
            rates.EnsureSuccessStatusCode();
        var traveler = await VerifiedAsync("Searching Traveller");

        var stays = await traveler.PostAsync<TravelSearchResultDto>($"{Base}/search", new TravelSearchRequest(TravelItemTypes.Stay, city, CheckIn: checkIn, CheckOut: checkIn.AddDays(2), Guests: 2));
        var pools = await traveler.PostAsync<TravelSearchResultDto>($"{Base}/search", new TravelSearchRequest(TravelItemTypes.Stay, city, Amenities: ["pool", "spa"]));
        var nearby = await traveler.PostAsync<TravelSearchResultDto>($"{Base}/search", new TravelSearchRequest(TravelItemTypes.Stay, Lat: -17.86, Lng: 25.86, RadiusKm: 5));
        var bigParty = await traveler.PostAsync<TravelSearchResultDto>($"{Base}/search", new TravelSearchRequest(TravelItemTypes.Stay, city, Guests: 40));
        var tours = await traveler.PostAsync<TravelSearchResultDto>($"{Base}/search", new TravelSearchRequest(TravelItemTypes.Tour, city, Guests: 2, TourKinds: ["river_cruise"]));
        var detail = await traveler.GetAsync<PropertyDto>($"{Base}/properties/{lodge.Id}?checkIn={checkIn:yyyy-MM-dd}&checkOut={checkIn.AddDays(2):yyyy-MM-dd}");
        var destinations = await traveler.GetAsync<List<TravelDestinationDto>>($"{Base}/destinations?count=24");

        var result = Assert.Single(stays.Results);
        Assert.Equal((lodge.Id, "night", true), (result.Id, result.PriceUnit, result.Available));
        Assert.Equal(120m, result.FromPrice); // the garden room is cheaper than the deluxe chalet's 220 + 180
        Assert.Contains(stays.Facets, f => f is { Field: "property_type", Code: "lodge", Count: 1 });
        Assert.Contains(stays.Facets, f => f is { Field: "board_plan", Code: "full_board" });
        Assert.Contains(stays.Facets, f => f is { Field: "stars", Code: "4" });
        Assert.Empty(pools.Results); // no spa
        Assert.Contains(nearby.Results, r => r.Id == lodge.Id && r.DistanceKm < 5);
        Assert.Empty(bigParty.Results);
        Assert.Single(tours.Results);
        // Explore's top destinations count the place's published stays and tours.
        Assert.Contains(destinations, d => d is { Stays: 1, Tours: 1 } && d.City == city);
        Assert.Equal(destinations.OrderByDescending(d => d.Stays + d.Tours).Select(d => d.City), destinations.Select(d => d.City));
        var deluxe = detail.Rooms.Single(r => r.Id == room.Id);
        Assert.Equal((3, 400m), (deluxe.AvailableUnits, deluxe.StayPrice));
        Assert.Equal([220m, 180m], deluxe.NightPrices);
    }

    [Fact]
    public async Task Booking_a_stay_pays_the_vendor_less_the_fee_opens_a_chat_and_issues_a_signed_invoice()
    {
        TransfersSucceed();
        var (vendor, lodge, room, _, _) = await VendorAsync("Royal Chundu");
        var traveler = await VerifiedAsync("Chipo Banda");
        var checkIn = Today.AddDays(45);

        var booking = await BookStayAsync(traveler, room, checkIn);

        Assert.Equal(TravelBookingStatuses.Confirmed, booking.Status);
        Assert.StartsWith("LT-", booking.Reference);
        Assert.Equal((360m, 18m, "ZW123456"), (booking.Total, booking.Fee, booking.IdNumber));
        // $360 from the traveller's wallet; Bridge keeps 5% for Ndeipi.
        var transfer = app.Bridge.RequestsTo(HttpMethod.Post, "/v0/transfers").Last().Json;
        Assert.Equal(("360", "18"), (transfer.GetProperty("amount").GetString(), transfer.GetProperty("developer_fee").GetString()));
        // The rooms for those nights are booked, not held.
        var nights = await app.DbAsync(db => db.RoomNights.Where(n => n.RoomTypeId == room.Id && n.Date >= checkIn && n.Date < checkIn.AddDays(2)).ToListAsync());
        Assert.All(nights, n => Assert.Equal((0, 1), (n.Held, n.Booked)));

        // FR-5.3: a chat between traveller and vendor, opened with the booking.
        Assert.NotNull(booking.ConversationId);
        var messages = await traveler.GetAsync<List<MessageDto>>($"api/conversations/{booking.ConversationId}/messages");
        Assert.Contains(messages, m => ContractJson.Read<TextPayload>(m.Payload)!.Text.Contains(booking.Reference));

        // FR-4.4: the invoice verifies against the published key.
        var invoice = await traveler.GetAsync<TravelInvoiceDto>($"{Base}/bookings/{booking.Id}/invoice");
        var published = await app.CreateClient().GetFromJsonAsync<JsonElement>($"{Base}/invoice-keys/{invoice.KeyId}");
        using (var key = ECDsa.Create())
        {
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(published.GetProperty("public_key_spki").GetString()!), out _);
            Assert.True(key.VerifyData(Encoding.UTF8.GetBytes(invoice.Canonical), Convert.FromBase64String(invoice.Signature), HashAlgorithmName.SHA256));
            Assert.False(key.VerifyData(Encoding.UTF8.GetBytes(invoice.Canonical.Replace("\"360.00\"", "\"36.00\"")), Convert.FromBase64String(invoice.Signature), HashAlgorithmName.SHA256));
        }
        Assert.DoesNotContain("ZW123456", invoice.Canonical);

        using var pdf = await traveler.Http.GetAsync($"{Base}/bookings/{booking.Id}/voucher.pdf");
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString((await pdf.Content.ReadAsByteArrayAsync())[..4]));

        var dashboard = await vendor.GetAsync<VendorDashboardDto>($"{Base}/vendor/dashboard");
        Assert.Contains(dashboard.Bookings, b => b.Id == booking.Id && b.IdNumber == "ZW123456");
        Assert.Equal((342m, 18m), (dashboard.EarnedThisMonth, dashboard.FeesThisMonth));
        var stranger = await VerifiedAsync("Nosy Neighbour");
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.Http.GetAsync($"{Base}/bookings/{booking.Id}")).StatusCode);
    }

    [Fact]
    public async Task Acceptance_1_a_thousand_people_try_for_the_last_room_and_exactly_one_gets_it()
    {
        var (_, _, room, _, _) = await VendorAsync("Last Room Lodge", units: 1);
        var checkIn = Today.AddDays(60);
        // One night already booked would leave nothing; this room has exactly one left on both nights.
        var attempts = Enumerable.Range(0, 1000).Select(async i =>
        {
            await using var scope = app.Services.CreateAsyncScope();
            var bookings = scope.ServiceProvider.GetRequiredService<TravelBookings>();
            var someone = new User { Id = Guid.NewGuid(), ClerkUserId = $"race_{i}", DisplayName = $"Racer {i}" };
            try
            {
                await bookings.HoldAsync(someone, new TravelHoldRequest(TravelItemTypes.Stay, room.Id, checkIn, checkIn.AddDays(2), 1, 2), default);
                return "held";
            }
            catch (ChatRejectedException e)
            {
                return e.Message;
            }
        }).ToList();
        var outcomes = await Task.WhenAll(attempts);

        Assert.Equal(1, outcomes.Count(o => o == "held"));
        Assert.Equal(999, outcomes.Count(o => o.Contains("fully booked")));
        var nights = await app.DbAsync(db => db.RoomNights.Where(n => n.RoomTypeId == room.Id && n.Date >= checkIn && n.Date < checkIn.AddDays(2)).ToListAsync());
        Assert.All(nights, n => Assert.Equal(1, n.Held));
    }

    [Fact]
    public async Task Acceptance_3_an_abandoned_hold_and_a_failed_payment_put_the_room_back_and_return_the_points()
    {
        var (_, lodge, room, _, _) = await VendorAsync("Release Lodge", units: 1);
        var traveler = await VerifiedAsync("Abandoning Traveller");
        var checkIn = Today.AddDays(70);
        var stay = new TravelHoldRequest(TravelItemTypes.Stay, room.Id, checkIn, checkIn.AddDays(1), 1, 2);

        var hold = await traveler.PostAsync<TravelHoldDto>($"{Base}/holds", stay);
        Assert.InRange(hold.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));
        var other = await VerifiedAsync("Waiting Traveller");
        using (var blocked = await other.Http.PostAsJsonAsync($"{Base}/holds", stay, ContractJson.Options))
            Assert.Contains("fully booked", await ProblemAsync(blocked));

        // Fifteen minutes pass: the sweeper puts the room back.
        await app.DbAsync(db => db.TravelHolds.Where(h => h.Id == hold.Id).ExecuteUpdateAsync(s => s.SetProperty(h => h.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        await TravelHoldSweeper.SweepAsync(app.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, default);
        using (var late = await traveler.Http.PostAsJsonAsync($"{Base}/holds/{hold.Id}/checkout", new TravelCheckoutRequest("Late Payer", null, null, null), ContractJson.Options))
            Assert.Contains("15 minutes ran out", await ProblemAsync(late));

        // The other traveller gets it, but the payment fails: back on sale, points returned.
        await AwardAsync(other.Id, 1000);
        var second = await other.PostAsync<TravelHoldDto>($"{Base}/holds", stay);
        TransfersFail();
        var failed = await other.PostAsync<TravelBookingDto>($"{Base}/holds/{second.Id}/checkout", new TravelCheckoutRequest("Other Traveller", null, null, null, RedeemPoints: true));

        Assert.Equal(TravelBookingStatuses.Failed, failed.Status);
        Assert.Equal(1000, (await other.GetAsync<PointsDto>(PointsContract.BasePath)).Balance);
        var detail = await other.GetAsync<PropertyDto>($"{Base}/properties/{lodge.Id}?checkIn={checkIn:yyyy-MM-dd}&checkOut={checkIn.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(1, detail.Rooms.Single(r => r.Id == room.Id).AvailableUnits);
    }

    [Fact]
    public async Task Believe_points_come_off_Ndeipis_fee_so_the_vendor_still_gets_their_full_share()
    {
        TransfersSucceed();
        var (_, _, room, _, _) = await VendorAsync("Points Lodge");
        var traveler = await VerifiedAsync("Loyal Traveller");
        await AwardAsync(traveler.Id, 5000); // $50 worth, more than the $18 fee

        var booking = await BookStayAsync(traveler, room, Today.AddDays(80), redeem: true);

        Assert.Equal((18m, 1800), (booking.Discount, booking.PointsRedeemed));
        var transfer = app.Bridge.RequestsTo(HttpMethod.Post, "/v0/transfers").Last().Json;
        // The traveller pays $342; Bridge keeps nothing for Ndeipi; the vendor gets $342 either way.
        Assert.Equal(("342", JsonValueKind.Undefined), (transfer.GetProperty("amount").GetString(), transfer.TryGetProperty("developer_fee", out var fee) ? fee.ValueKind : JsonValueKind.Undefined));
        Assert.Equal(3200, (await traveler.GetAsync<PointsDto>(PointsContract.BasePath)).Balance);
    }

    [Fact]
    public async Task A_tour_booking_includes_mandatory_extras_and_a_rescheduled_or_cancelled_departure_reaches_every_traveller()
    {
        TransfersSucceed();
        var (vendor, _, _, cruise, departure) = await VendorAsync("Zambezi Boats");
        var traveler = await VerifiedAsync("Tendai Moyo");
        var transfer = cruise.AddOns.Single(a => !a.Mandatory);

        var hold = await traveler.PostAsync<TravelHoldDto>($"{Base}/holds", new TravelHoldRequest(TravelItemTypes.Tour, departure.Id, Quantity: 2, AddOnIds: [transfer.Id]));
        var booking = await traveler.PostAsync<TravelBookingDto>($"{Base}/holds/{hold.Id}/checkout", new TravelCheckoutRequest("Tendai Moyo", null, null, null));
        using (var full = await traveler.Http.PostAsJsonAsync($"{Base}/holds", new TravelHoldRequest(TravelItemTypes.Tour, departure.Id, Quantity: 3), ContractJson.Options))
            Assert.Contains("aren't 3 seats", await ProblemAsync(full));

        // 2 × $65, the $10 park fee each, and one $15 transfer.
        Assert.Equal(165m, hold.Total);
        Assert.Equal(["2 × Zambezi sunset cruise", "National park fee × 2", "Hotel transfer"], hold.Lines.Select(l => l.Label));
        Assert.Equal(TravelBookingStatuses.Confirmed, booking.Status);

        var later = departure.StartsAt.AddDays(1);
        var moved = await vendor.PostAsync<DepartureDto>($"{Base}/vendor/tours/{cruise.Id}/departures/{departure.Id}/reschedule",
            new RescheduleRequest(later, false, "High winds forecast on the river"));
        var afterMove = await traveler.GetAsync<TravelBookingDto>($"{Base}/bookings/{booking.Id}");
        var chat = await traveler.GetAsync<List<MessageDto>>($"api/conversations/{booking.ConversationId}/messages");

        Assert.Equal(DepartureStatuses.Rescheduled, moved.Status);
        Assert.Contains("High winds", afterMove.Notice);
        Assert.Equal(later, afterMove.Items[0].StartsAt);
        Assert.Contains(chat, m => m.SenderId == vendor.Id && ContractJson.Read<TextPayload>(m.Payload)!.Text.Contains("High winds"));

        await vendor.PostAsync<DepartureDto>($"{Base}/vendor/tours/{cruise.Id}/departures/{departure.Id}/reschedule",
            new RescheduleRequest(null, true, "River closed by the park authority"));
        var canceled = await traveler.GetAsync<TravelBookingDto>($"{Base}/bookings/{booking.Id}");
        var refund = app.Bridge.RequestsTo(HttpMethod.Post, "/v0/transfers").Last().Json;

        Assert.Equal(TravelBookingStatuses.Canceled, canceled.Status);
        Assert.Contains("River closed", canceled.Notice);
        Assert.Equal("165", refund.GetProperty("amount").GetString());
        Assert.Equal(0, await app.DbAsync(db => db.TourDepartures.Where(d => d.Id == departure.Id).Select(d => d.Booked).FirstAsync()));
    }

    [Fact]
    public async Task The_vendor_checks_a_guest_in_from_the_vouchers_rolling_code_once_and_only_their_own()
    {
        TransfersSucceed();
        var (vendor, _, room, _, _) = await VendorAsync("Check-in Lodge");
        var (otherVendor, _, _, _, _) = await VendorAsync("Another Lodge");
        var traveler = await VerifiedAsync("Rudo Check");
        var booking = await BookStayAsync(traveler, room, Today.AddDays(5));

        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using (var bound = await traveler.Http.PutAsJsonAsync($"{Base}/bookings/{booking.Id}/key",
                   new BindTravelKeyRequest(Convert.ToBase64String(device.ExportSubjectPublicKeyInfo())), ContractJson.Options))
            bound.EnsureSuccessStatusCode();
        string Code(long windowShift = 0, ECDsa? signer = null)
        {
            var window = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / TravelContract.QrWindowSeconds + windowShift;
            return TravelContract.QrText(booking.Id, window, Convert.ToBase64String((signer ?? device).SignData(TravelContract.QrPayload(booking.Id, window), HashAlgorithmName.SHA256)));
        }
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var wrongVendor = await otherVendor.PostAsync<CheckInResultDto>($"{Base}/vendor/check-in", new CheckInRequest(Code()));
        var forged = await vendor.PostAsync<CheckInResultDto>($"{Base}/vendor/check-in", new CheckInRequest(Code(signer: stranger)));
        var stale = await vendor.PostAsync<CheckInResultDto>($"{Base}/vendor/check-in", new CheckInRequest(Code(windowShift: -20)));
        var first = await vendor.PostAsync<CheckInResultDto>($"{Base}/vendor/check-in", new CheckInRequest(Code()));
        var again = await vendor.PostAsync<CheckInResultDto>($"{Base}/vendor/check-in", new CheckInRequest(Code()));

        Assert.Equal(CheckInOutcomes.NotYours, wrongVendor.Outcome);
        Assert.Equal(CheckInOutcomes.Invalid, forged.Outcome);
        Assert.Equal(CheckInOutcomes.Invalid, stale.Outcome);
        Assert.Equal(CheckInOutcomes.CheckedIn, first.Outcome);
        Assert.Equal(TravelBookingStatuses.CheckedIn, first.Booking!.Status);
        Assert.Equal(CheckInOutcomes.AlreadyCheckedIn, again.Outcome);
        using (var cancel = await vendor.Http.PostAsJsonAsync($"{Base}/vendor/bookings/{booking.Id}/cancel", new { reason = "x" }))
            Assert.Contains("already checked in", await ProblemAsync(cancel));
    }

    [Fact]
    public async Task A_closed_night_cant_be_booked_vendors_cant_book_themselves_and_admins_can_suspend_a_vendor()
    {
        var (vendor, lodge, room, _, _) = await VendorAsync("Closing Lodge");
        var traveler = await VerifiedAsync("Blocked Traveller");
        var admin = await app.CreateUserAsync("Ndeipi Admin", roles: [FinanceContract.AdminRole]);
        var checkIn = Today.AddDays(90);
        using (var closed = await vendor.Http.PutAsJsonAsync($"{Base}/vendor/properties/{lodge.Id}/rooms/{room.Id}/rates",
                   new SetRatesRequest(checkIn.AddDays(1), checkIn.AddDays(1), null, true), ContractJson.Options))
            closed.EnsureSuccessStatusCode();

        using var throughClosed = await traveler.Http.PostAsJsonAsync($"{Base}/holds", new TravelHoldRequest(TravelItemTypes.Stay, room.Id, checkIn, checkIn.AddDays(3), 1, 2), ContractJson.Options);
        using var own = await vendor.Http.PostAsJsonAsync($"{Base}/holds", new TravelHoldRequest(TravelItemTypes.Stay, room.Id, checkIn, checkIn.AddDays(1), 1, 2), ContractJson.Options);
        using (var notAdmin = await traveler.Http.PutAsJsonAsync($"{Base}/admin/vendor/{vendor.Id}/status", new SetStatusRequest("suspended", null), ContractJson.Options))
            Assert.Contains("Only Ndeipi admins", await ProblemAsync(notAdmin));
        (await admin.Http.PutAsJsonAsync($"{Base}/admin/vendor/{vendor.Id}/status", new SetStatusRequest("suspended", "Complaints"), ContractJson.Options)).EnsureSuccessStatusCode();
        var search = await traveler.PostAsync<TravelSearchResultDto>($"{Base}/search", new TravelSearchRequest(TravelItemTypes.Stay, "Closing Lodge"));

        Assert.Contains("fully booked", await ProblemAsync(throughClosed));
        Assert.Contains("your own listing", await ProblemAsync(own));
        Assert.Empty(search.Results);
    }

    [Fact]
    public async Task Listing_photos_are_reencoded_and_served_only_through_expiring_links()
    {
        var (vendor, lodge, _, _, _) = await VendorAsync("Photo Lodge");
        using var bitmap = new SKBitmap(1200, 800);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.SeaGreen);
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "file", "deck.png");

        using var upload = await vendor.Http.PostAsync($"{Base}/vendor/stay/{lodge.Id}/photos", form);
        var photos = (await upload.Content.ReadFromJsonAsync<List<TravelPhotoDto>>(ContractJson.Options))!;
        using var image = await app.CreateClient().GetAsync(photos[0].ThumbUrl);
        using var tampered = await app.CreateClient().GetAsync(photos[0].ThumbUrl[..^4] + "AAAA");

        Assert.Equal(HttpStatusCode.OK, upload.StatusCode);
        Assert.Equal("image/jpeg", image.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, tampered.StatusCode);
    }

    async Task AwardAsync(Guid userId, int points)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<PointsService>().AwardAsync(userId, points, "test", Guid.NewGuid().ToString("N"), "Test points", default);
    }
}
