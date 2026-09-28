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

        var me = await app.CreateUserAsync("Ty Preview", "ty.preview@example.test", roles: [EventsContract.OrganizerRole]);
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

        // Sign in as the phone app and the web do, for tokens the browser can use and refresh.
        var options = new ClientOptions { ApiBaseUrl = site, RedirectUri = TestApp.RedirectUri };
        var store = new InMemoryTokenStore();
        var auth = new AuthService(new HttpClient { BaseAddress = site }, store, new SimulatedClerkSignIn(app, me), options, TimeProvider.System);
        await auth.SignInAsync();
        await File.WriteAllTextAsync(tokensFile, ContractJson.Write((await store.LoadAsync())!));

        await Task.Delay(TimeSpan.FromMinutes(minutes));
    }
}
