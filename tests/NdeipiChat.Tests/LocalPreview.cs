using System.Net.Http.Json;
using NdeipiChat.Client;
using NdeipiChat.Client.Auth;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Not a test: runs the real API and web app on http://localhost:{port} with the fake Clerk, seeds a
/// few people, a long chat, gigs and groups, and writes a signed-in session to a file -- so the web
/// app can be clicked through in a browser without a real Clerk account. Does nothing unless
/// NDEIPI_PREVIEW_MINUTES is set, e.g.
///   NDEIPI_PREVIEW_MINUTES=30 NDEIPI_PREVIEW_TOKENS=tokens.json dotnet test --filter LocalPreview
/// then in the browser: localStorage["ndeipi.tokens"] = (the file's contents), and reload.
/// </summary>
public sealed class LocalPreview
{
    public const int Port = 5250;

    [Fact]
    public async Task Serve_the_web_app_for_a_browser()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("NDEIPI_PREVIEW_MINUTES"), out var minutes))
            return;
        var tokensFile = Environment.GetEnvironmentVariable("NDEIPI_PREVIEW_TOKENS") ?? Path.Combine(Path.GetTempPath(), "ndeipi-preview-tokens.json");

        await using var app = new TestApp { PreviewPort = Port };
        app.UseKestrel(Port);
        app.StartServer();
        var site = new Uri($"http://localhost:{Port}/");

        var me = await app.CreateUserAsync("Ty Preview", "ty.preview@example.test", roles: [EventsContract.OrganizerRole, DonationsContract.VerifierRole, FinanceContract.AdminRole]);
        var tendai = await app.CreateUserAsync("Tendai Chikwanha", "tendai@example.test");
        var rudo = await app.CreateUserAsync("Rudo Moyo", "rudo@example.test");

        foreach (var (friend, email) in new[] { (tendai, "tendai@example.test"), (rudo, "rudo@example.test") })
        {
            await me.PostAsync<AddShamwariResponse>("api/shamwaris", new AddShamwariRequest(email));
            var incoming = await friend.GetAsync<ShamwariListDto>("api/shamwaris");
            await friend.PostAsync<ShamwariListDto>($"api/shamwaris/requests/{incoming.Incoming.Single().Id}/accept", new { });
        }

        // A chat long enough to scroll.
        var chat = await me.StartDirectChatAsync(tendai);
        for (var i = 1; i <= 30; i++)
        {
            var sender = i % 3 == 0 ? me : tendai;
            await sender.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages", new SendMessageRequest(chat.Id, MessageKinds.Text,
                ContractJson.ToElement(new TextPayload($"Message {i}: {(i % 2 == 0 ? "Makadii? Are we still meeting at the market tomorrow?" : "Ndiripo, see you there.")}")), Guid.NewGuid()));
        }
        await me.StartDirectChatAsync(rudo);

        // Gigs: a worker profile and a few open gigs in different categories.
        using (var put = await tendai.Http.PutAsJsonAsync("api/gigs/profile", new SaveGigProfileRequest("Photographer and videographer", ["photography", "video"], "Avondale", -17.80, 31.04), ContractJson.Options))
            put.EnsureSuccessStatusCode();
        var gigs = new[] { ("Photograph a wedding", "photography"), ("Deliver a parcel to Borrowdale", "delivery"), ("Fix a leaking roof", "construction"), ("Maths tutoring for Form 4", "tutoring") };
        foreach (var (title, skill) in gigs)
            await rudo.PostAsync<GigDto>("api/gigs", new CreateGigRequest(title, "Details to agree in chat.", skill, -17.83, 31.05, "Harare", "25"));

        using (var available = await tendai.Http.PutAsJsonAsync("api/gigs/profile/availability", new AvailabilityRequest(true), ContractJson.Options))
            available.EnsureSuccessStatusCode();

        // Social: bios, follows, a public and a private group, and some posts.
        foreach (var (user, bio, city) in new[] { (me, "Building Ndeipi.", "Harare"), (tendai, "Photographer. Weddings, events, portraits.", "Harare"), (rudo, "Farmer and market trader.", "Mutare") })
            using (var saved = await user.Http.PutAsJsonAsync("api/social/profile", new SaveSocialProfileRequest(bio, city, null), ContractJson.Options))
                saved.EnsureSuccessStatusCode();
        (await me.Http.PostAsync($"api/social/follows/{tendai.Id}", null)).EnsureSuccessStatusCode();
        (await rudo.Http.PostAsync($"api/social/follows/{me.Id}", null)).EnsureSuccessStatusCode();
        var photographers = await tendai.PostAsync<GroupDetailDto>("api/social/groups",
            new SaveGroupRequest("Harare Photographers", "Share your best shots of the city and swap tips.", "Be kind\nCredit other people's work\nNo spam", "📷", "pink", false));
        await rudo.PostAsync<GroupDetailDto>("api/social/groups", new SaveGroupRequest("Mutare Farmers", "Prices, weather and help for farmers in Manicaland.", null, "🌾", "green", false));
        await me.PostAsync<GroupDetailDto>($"api/social/groups/{photographers.Group.Id}/join", new { });
        foreach (var (user, text, group) in new[]
        {
            (tendai, "Golden hour at Domboshava today. Who's coming next weekend?", (Guid?)photographers.Group.Id),
            (rudo, "Tomatoes are $2 a crate at Sakubva this week.", null),
            (me, "Welcome to the new Social section!", null)
        })
        {
            using var form = new MultipartFormDataContent { { new StringContent(text), "caption" } };
            if (group is { } g)
                form.Add(new StringContent(g.ToString()), "groupId");
            (await user.Http.PostAsync("api/posts", form)).EnsureSuccessStatusCode();
        }

        // POS: a shop with a counted shirt (3 in stock) and an uncounted burger; PIN 2580. Both
        // wallets are verified, and the stand-in Bridge settles transfers at once, so a Ndeipi Pay
        // payment completes as soon as the customer confirms it.
        foreach (var user in new[] { me, tendai })
            await app.DbAsync(db =>
            {
                db.BankingProfiles.Add(new NdeipiChat.Api.Data.BankingProfile
                {
                    UserId = user.Id, BridgeCustomerId = $"cust_{user.Id:N}", KycLinkId = $"kyc_{user.Id:N}",
                    KycStatus = NdeipiChat.Api.Data.BankingProfile.Approved, TosStatus = NdeipiChat.Api.Data.BankingProfile.Approved,
                    WalletId = $"wal_{user.Id:N}", WalletChain = "solana", WalletAddress = $"addr_{user.Id:N}", UpdatedAt = DateTimeOffset.UtcNow
                });
                return db.SaveChangesAsync();
            });
        app.Bridge.On(HttpMethod.Post, "/v0/transfers", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = $"tr_{Guid.NewGuid():N}", state = "payment_processed" })
        });
        var shop = await me.PostAsync<PosMerchantDto>("api/pos/merchants", new CreateMerchantRequest("Arcadia Sports Club Bar & Restaurant", "usd", true, "Arcadia", 15));
        using (var pin = await me.Http.PutAsJsonAsync($"api/pos/merchants/{shop.Id}/pin", new SetPinRequest("2580"), ContractJson.Options))
            pin.EnsureSuccessStatusCode();
        var shirt = await me.PostAsync<PosProductDto>($"api/pos/merchants/{shop.Id}/products",
            new SaveProductRequest(null, "Shirt", "👕", "SHIRT", "600000000100", 45m, null, true, 1, null, null));
        await me.PostAsync<PosProductDto>($"api/pos/merchants/{shop.Id}/products",
            new SaveProductRequest(null, "Burger", "🍔", "BURG", null, 8m, null, true, 0, null, null, TrackStock: false));
        await me.PostAsync<PosStockLevelDto>($"api/pos/stores/{shop.Stores[0].Id}/stock", new StockAdjustRequest(shirt.Id, null, 3, "Delivery"));

        // Donations: a live campaign by me with a couple of gifts from Tendai (one anonymous); the
        // stand-in Bridge settles them at once, so they count and earn points straight away.
        var campaign = await me.PostAsync<CampaignDto>("api/donations/campaigns", new SaveCampaignRequest("School shoes for Mbare kids",
            "Every child at the home starts term in proper shoes.", "Forty children, forty pairs of shoes. $25 buys one pair.", "Mbare Children's Home", "👟", 1000, DateTimeOffset.UtcNow.AddDays(21)));
        await me.PostAsync<CampaignDto>($"api/donations/campaigns/{campaign.Id}/publish", new { });
        await me.PostAsync<CampaignDto>("api/donations/campaigns", new SaveCampaignRequest("Borehole for Chivi",
            "Clean water for a village of 300.", null, "Chivi Community Trust", "💧", 2500, null));
        foreach (var (amount, anonymous, message) in new[] { (25m, false, "Go well!"), (10m, true, (string?)null) })
            await tendai.PostAsync<DonationDto>($"api/donations/campaigns/{campaign.Id}/donations", new DonateRequest(amount, anonymous, message));

        // Finance: prices for two machines, committees for Mtenguleni (me on the village one), and an
        // application from Tendai waiting on it, plus a draft of mine.
        foreach (var (id, hire, buy, running) in new[] { (Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a03"), 2000m, 45000m, 500m), (Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a01"), 1500m, 30000m, 400m) })
        {
            var q = (await me.GetAsync<List<LoanEquipmentDto>>("api/finance/equipment?all=true")).Single(e => e.Id == id);
            using var priced = await me.Http.PutAsJsonAsync($"api/finance/equipment/{id}", new SaveEquipmentRequest(q.Name, q.Description, q.ClusterType, q.Mtp, hire, buy, running, true, q.Order), ContractJson.Options);
            priced.EnsureSuccessStatusCode();
        }
        foreach (var stage in LoanStages.Order)
            await me.PostAsync<LoanCommitteeDto>("api/finance/committees", new SaveCommitteeRequest(stage,
                stage == LoanStages.Absa ? "Absa Chipata branch" : $"Mtenguleni {LoanStages.Label(stage)}", "Eastern", "Kasenengwa", "Mtenguleni", null,
                stage == LoanStages.Village ? [me.Id] : [rudo.Id]));
        var loan = await tendai.PostAsync<LoanApplicationDto>("api/finance/applications", new SaveLoanApplicationRequest(
            new EligibilityDto(true, RegistrationBodies.Cooperatives, LicenceTypes.Artisanal, true), "Mtenguleni Gold Cooperative", "cooperative", "COOP/EP/2291",
            "Eastern", "Kasenengwa", "Mtenguleni", "Chimkoka", ClusterTypes.Processing,
            [new LoanItemRequest(Guid.Parse("6a0f1c52-3f7e-4d0b-9a51-1d6f0c2b7a03"), false)], 3));
        foreach (var (kind, _, _) in LoanDocumentKinds.All)
        {
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(System.Text.Encoding.ASCII.GetBytes("%PDF-1.4\n% preview\n")), "file", $"{kind}.pdf");
            (await tendai.Http.PostAsync($"api/finance/applications/{loan.Id}/documents/{kind}", form)).EnsureSuccessStatusCode();
        }
        await tendai.PostAsync<LoanApplicationDto>($"api/finance/applications/{loan.Id}/submit", new SubmitLoanRequest(true));
        await me.PostAsync<LoanApplicationDto>("api/finance/applications", new SaveLoanApplicationRequest(
            new EligibilityDto(true, RegistrationBodies.Pacra, LicenceTypes.SmallScale, true), "Kasenengwa Miners Club", "club", null,
            "Eastern", null, null, null, null, null, null));

        // Market: a "Hooves & Feathers" group with a photo, my goat listing (with a hashtag) and
        // Tendai's barter offer on it.
        var hooves = await me.PostAsync<ConversationDto>("api/conversations",
            new CreateConversationRequest(ConversationType.Group, [tendai.Id, rudo.Id], "Hooves & Feathers Network"));
        async Task<MediaItem> PhotoAsync(TestUser who, SkiaSharp.SKColor color)
        {
            using var bitmap = new SkiaSharp.SKBitmap(1200, 900);
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
                canvas.Clear(color);
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, 80).ToArray()), "file", "photo.jpg");
            using var uploaded = await who.Http.PostAsync($"api/conversations/{hooves.Id}/media", form);
            return (await uploaded.Content.ReadFromJsonAsync<MediaItem>(ContractJson.Options))!;
        }
        var pen = await PhotoAsync(rudo, new SkiaSharp.SKColor(0x8B, 0x6B, 0x3D));
        await rudo.PostAsync<MessageDto>($"api/conversations/{hooves.Id}/messages", new SendMessageRequest(hooves.Id, MessageKinds.Media,
            ContractJson.ToElement(new MediaPayload([pen], "Our upcoming Pekin parent flock for Maposa area #PurePekinPoultry")), Guid.NewGuid()));
        var goat = await PhotoAsync(me, new SkiaSharp.SKColor(0x6B, 0x8E, 0x23));
        var listed = await me.PostAsync<MessageDto>($"api/conversations/{hooves.Id}/messages", new SendMessageRequest(hooves.Id, MessageKinds.MarketListing,
            ContractJson.ToElement(new MarketListingPayload(Guid.Empty, Guid.Empty, "Boer goat buck #QualityGenetics", "goats", "Boer", 1, 12000m, "ZMW",
                "Monze", "5 years old", "Strong, healthy buck from our #QualityGenetics line. Vaccinated.", true, "6 better crossbreed females",
                ListingStatuses.Available, [goat], [])), Guid.NewGuid()));
        var goatListing = ContractJson.Read<MarketListingPayload>(listed.Payload)!.ListingId;
        await tendai.PostAsync<MessageDto>($"api/conversations/{hooves.Id}/messages", new SendMessageRequest(hooves.Id, MessageKinds.MarketOffer,
            ContractJson.ToElement(new MarketOfferPayload(Guid.Empty, goatListing, "", Guid.Empty, OfferKinds.Barter, null, null,
                "Exchange with 6 better crossbreed females acceptable (small ones?)")), Guid.NewGuid()));

        // Creators: my page with two tiers and a public, a members-only and a pay-per-view post;
        // Tendai subscribes (and shares who he is). My wallet shows $120 for the earnings page.
        app.Bridge.On(HttpMethod.Get, $"/v0/customers/cust_{me.Id:N}/wallets/wal_{me.Id:N}", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { id = $"wal_{me.Id:N}", chain = "solana", address = "So1ana", balances = new[] { new { balance = "120.00", currency = "usdc", chain = "solana" } } })
        });
        using (var page = await me.Http.PutAsJsonAsync("api/creators/profile", new SaveCreatorRequest("Ty's Farm Lab", "farming",
            "Practical lessons on goats, poultry and small-scale farming in Zambia and Zimbabwe.", [new CreatorLink("YouTube", "https://youtube.com/@ndeipi")]), ContractJson.Options))
            page.EnsureSuccessStatusCode();
        var supporter = await me.PostAsync<CreatorTierDto>("api/creators/studio/tiers", new SaveCreatorTierRequest("Supporter", "Members-only posts", 3m, 30m, 1));
        var insider = await me.PostAsync<CreatorTierDto>("api/creators/studio/tiers", new SaveCreatorTierRequest("Insider", "Everything, plus monthly Q&A", 10m, null, 2));
        await me.PostAsync<CreatorPostDto>("api/creators/studio/posts", new SaveCreatorPostRequest("Welcome to the Farm Lab",
            "Every week: one practical lesson you can use on your farm.", null, PostAccess.Public, null, null, null));
        await me.PostAsync<CreatorPostDto>("api/creators/studio/posts", new SaveCreatorPostRequest("Feeding Boer kids for faster growth",
            "The feeding plan we use from week 2 to week 12, with costs in kwacha.", null, PostAccess.Tier, supporter.Id, null, null));
        await me.PostAsync<CreatorPostDto>("api/creators/studio/posts", new SaveCreatorPostRequest("Live Q&A recording",
            "An hour of your questions on poultry disease.", null, PostAccess.PayPerView, insider.Id, 2m, null));
        await tendai.PostAsync<CreatorSubscriptionDto>($"api/creators/{me.Id}/subscribe", new CreatorSubscribeRequest(supporter.Id, BillingPeriods.Monthly, ShareProfile: true));

        // Lets Travel: Tendai runs a lodge and a river cruise at Victoria Falls; I have a stay booked there.
        using (var vendor = await tendai.Http.PutAsJsonAsync("api/travel/vendor", new SaveVendorRequest("Zambezi Sands Travel", "Lodges and river trips around the Falls.", "+260 97 555 0101", null), ContractJson.Options))
            vendor.EnsureSuccessStatusCode();
        var lodge = await tendai.PostAsync<PropertyDto>("api/travel/vendor/properties", new SavePropertyRequest("Zambezi Sands River Lodge", "lodge",
            "Twelve thatched chalets on the banks of the Zambezi, twenty minutes upstream from the Falls. Sundowners on the deck, elephants on the lawn.",
            "Victoria Falls", "Zambia", "Plot 9, Riverside Drive, Livingstone", -17.85, 25.85, 4, ["wifi", "pool", "game_drives", "boat_trips", "restaurant"], "moderate", "14:00", "10:00"));
        var chalet = await tendai.PostAsync<RoomTypeDto>($"api/travel/vendor/properties/{lodge.Id}/rooms",
            new SaveRoomTypeRequest("Deluxe river chalet", "deluxe", "full_board", 2, 4, 240m, "Private deck over the river, outdoor shower."));
        await tendai.PostAsync<RoomTypeDto>($"api/travel/vendor/properties/{lodge.Id}/rooms",
            new SaveRoomTypeRequest("Garden room", "standard", "bed_breakfast", 3, 6, 140m, "Garden views, sleeps a family of three."));
        foreach (var colour in new[] { new SkiaSharp.SKColor(0xC2, 0x41, 0x0C), new SkiaSharp.SKColor(0x1F, 0xA9, 0x71) })
        {
            using var bitmap = new SkiaSharp.SKBitmap(1200, 800);
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
                canvas.Clear(colour);
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray()), "file", "lodge.png");
            (await tendai.Http.PostAsync($"api/travel/vendor/stay/{lodge.Id}/photos", form)).EnsureSuccessStatusCode();
        }
        (await tendai.Http.PostAsJsonAsync($"api/travel/vendor/stay/{lodge.Id}/publish", new { published = true })).EnsureSuccessStatusCode();
        var cruise = await tendai.PostAsync<TourDto>("api/travel/vendor/tours", new SaveTourRequest("Zambezi sunset cruise", "river_cruise",
            "Two and a half hours upriver at sundown: hippos, elephants on the islands, and the spray of the Falls behind you.",
            "Victoria Falls", "Zambia", -17.9, 25.84, 2.5, 12, "easy", 65m, true, "Drinks and snacks", "Boat club jetty, Riverside Drive"));
        await tendai.PostAsync<TourAddOnDto>($"api/travel/vendor/tours/{cruise.Id}/addons", new SaveAddOnRequest("National park fee", 10m, true, true));
        await tendai.PostAsync<TourAddOnDto>($"api/travel/vendor/tours/{cruise.Id}/addons", new SaveAddOnRequest("Hotel transfer", 15m, false, false));
        for (var day = 3; day <= 9; day += 2)
            await tendai.PostAsync<DepartureDto>($"api/travel/vendor/tours/{cruise.Id}/departures", new SaveDepartureRequest(DateTimeOffset.UtcNow.Date.AddDays(day).AddHours(14.5), null, "Mwila"));
        (await tendai.Http.PostAsJsonAsync($"api/travel/vendor/tour/{cruise.Id}/publish", new { published = true })).EnsureSuccessStatusCode();
        // More places, so Explore's top destinations and recommendations have something to show.
        async Task ListAsync(string type, Guid id, SkiaSharp.SKColor top, SkiaSharp.SKColor bottom)
        {
            using var bitmap = new SkiaSharp.SKBitmap(1200, 800);
            using (var canvas = new SkiaSharp.SKCanvas(bitmap))
            using (var paint = new SkiaSharp.SKPaint { Shader = SkiaSharp.SKShader.CreateLinearGradient(new(0, 0), new(0, 800), [top, bottom], SkiaSharp.SKShaderTileMode.Clamp) })
                canvas.DrawRect(0, 0, 1200, 800, paint);
            using var form = new MultipartFormDataContent();
            form.Add(new ByteArrayContent(SkiaSharp.SKImage.FromBitmap(bitmap).Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray()), "file", "photo.png");
            (await tendai.Http.PostAsync($"api/travel/vendor/{type}/{id}/photos", form)).EnsureSuccessStatusCode();
            (await tendai.Http.PostAsJsonAsync($"api/travel/vendor/{type}/{id}/publish", new { published = true })).EnsureSuccessStatusCode();
        }
        var places = new[]
        {
            ("Hwange Bush Camp", "bush_camp", "Hwange", "Zimbabwe", -18.73, 26.95, 3, 180m, new SkiaSharp.SKColor(0xD9, 0x77, 0x06), new SkiaSharp.SKColor(0x78, 0x35, 0x0F)),
            ("Meikles Garden Hotel", "hotel", "Harare", "Zimbabwe", -17.83, 31.05, 5, 210m, new SkiaSharp.SKColor(0x3D, 0x7B, 0xF7), new SkiaSharp.SKColor(0x1E, 0x29, 0x3B)),
            ("Bo-Kaap Guesthouse", "guesthouse", "Cape Town", "South Africa", -33.92, 18.41, 4, 95m, new SkiaSharp.SKColor(0xF4, 0x72, 0xB6), new SkiaSharp.SKColor(0x1E, 0x3A, 0x8A)),
            ("Chobe Riverfront Lodge", "lodge", "Kasane", "Botswana", -17.81, 25.15, 4, 260m, new SkiaSharp.SKColor(0x10, 0xB9, 0x81), new SkiaSharp.SKColor(0x06, 0x4E, 0x3B)),
            ("Stone Town Rooftops", "apartment", "Zanzibar", "Tanzania", -6.16, 39.19, 3, 85m, new SkiaSharp.SKColor(0x06, 0xB6, 0xD4), new SkiaSharp.SKColor(0x0E, 0x74, 0x90)),
        };
        foreach (var (name, kind, city, country, lat, lng, stars, price, top, bottom) in places)
        {
            var place = await tendai.PostAsync<PropertyDto>("api/travel/vendor/properties", new SavePropertyRequest(name, kind,
                $"A {stars}-star {kind.Replace('_', ' ')} in {city}.", city, country, null, lat, lng, stars, ["wifi", "restaurant"], "flexible", "14:00", "10:00"));
            await tendai.PostAsync<RoomTypeDto>($"api/travel/vendor/properties/{place.Id}/rooms", new SaveRoomTypeRequest("Double room", "standard", "bed_breakfast", 2, 5, price, null));
            await ListAsync("stay", place.Id, top, bottom);
        }
        var walk = await tendai.PostAsync<TourDto>("api/travel/vendor/tours", new SaveTourRequest("Hwange walking safari", "walking_safari",
            "A morning on foot with an armed guide, reading tracks to the waterholes.", "Hwange", "Zimbabwe", -18.7, 26.9, 4, 8, "moderate", 90m, true, "Breakfast in the bush", "Main camp gate"));
        await tendai.PostAsync<DepartureDto>($"api/travel/vendor/tours/{walk.Id}/departures", new SaveDepartureRequest(DateTimeOffset.UtcNow.Date.AddDays(5).AddHours(5.5), null, "Tafadzwa"));
        await ListAsync("tour", walk.Id, new SkiaSharp.SKColor(0xFB, 0xBF, 0x24), new SkiaSharp.SKColor(0x92, 0x40, 0x0E));

        var checkIn = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(14));
        var stayHold = await me.PostAsync<TravelHoldDto>("api/travel/holds", new TravelHoldRequest(TravelItemTypes.Stay, chalet.Id, checkIn, checkIn.AddDays(3), 1, 2));
        await me.PostAsync<TravelBookingDto>($"api/travel/holds/{stayHold.Id}/checkout", new TravelCheckoutRequest("Ty Preview", "+263 77 000 0000", null, "Anniversary trip!"));

        // Sign in as the phone app and the web do, for tokens the browser can use and refresh.
        var options = new ClientOptions { ApiBaseUrl = site, RedirectUri = TestApp.RedirectUri };
        foreach (var (user, file) in new[] { (me, tokensFile), (tendai, tokensFile + ".tendai") })
        {
            var store = new InMemoryTokenStore();
            var auth = new AuthService(new HttpClient { BaseAddress = site }, store, new SimulatedClerkSignIn(app, user), options, TimeProvider.System);
            await auth.SignInAsync();
            await File.WriteAllTextAsync(file, ContractJson.Write((await store.LoadAsync())!));
        }

        await Task.Delay(TimeSpan.FromMinutes(minutes));
    }
}
