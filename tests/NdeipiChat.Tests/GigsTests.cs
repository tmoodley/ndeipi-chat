using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class GigsTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = GigsContract.BasePath;

    /// <summary>Each test works in its own patch of the world, so workers from other tests aren't nearby.</summary>
    static (double Lat, double Lng) Somewhere() => (Random.Shared.NextDouble() * 100 - 50, Random.Shared.NextDouble() * 300 - 150);

    /// <summary>About <paramref name="km"/> north of a point.</summary>
    static (double Lat, double Lng) North((double Lat, double Lng) from, double km) => (from.Lat + km / 111.2, from.Lng);

    async Task<TestUser> WorkerAsync(string name, (double Lat, double Lng) at, string skill = "photography", bool available = true)
    {
        var worker = await app.CreateUserAsync(name);
        await Put<GigProfileDto>(worker, $"{Base}/profile", new SaveGigProfileRequest($"{name} works hard", [skill], "Test", at.Lat, at.Lng));
        if (available)
            await Put<GigProfileDto>(worker, $"{Base}/profile/availability", new AvailabilityRequest(true));
        return worker;
    }

    static async Task<T> Put<T>(TestUser user, string path, object body)
    {
        using var response = await user.Http.PutAsJsonAsync(path, body, ContractJson.Options);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(ContractJson.Options))!;
    }

    static CreateGigRequest Shoot((double Lat, double Lng) at, string budget = "25.5", Guid? conversationId = null, Guid? workerId = null) =>
        new("Photograph a wedding", "Saturday, 4 hours", "photography", at.Lat, at.Lng, "Borrowdale", budget, conversationId, workerId);

    [Fact]
    public async Task Others_see_a_workers_rough_area_only()
    {
        var at = Somewhere();
        var worker = await WorkerAsync("Precise Worker", (at.Lat + 0.001234, at.Lng + 0.005678));

        var own = await worker.GetAsync<GigProfileDto>($"{Base}/profile");
        Assert.Equal(at.Lat + 0.001234, own.Latitude, 9);

        var stranger = await app.CreateUserAsync("Map Stranger");
        var seen = await stranger.GetAsync<GigProfileDto>($"{Base}/profiles/{worker.Id}");
        Assert.Equal(GigsContract.Approximate(at.Lat + 0.001234), seen.Latitude);
        Assert.Contains((await stranger.GetAsync<GigMapDto>($"{Base}/map")).Workers, w => w.UserId == worker.Id && w.Longitude == GigsContract.Approximate(at.Lng + 0.005678));

        using var bad = await worker.Http.PutAsJsonAsync($"{Base}/profile", new SaveGigProfileRequest("x", ["juggling"], null, 0, 0), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Gigs_are_offered_to_the_nearest_available_workers_with_the_skill()
    {
        var at = Somewhere();
        var near = new List<TestUser>();
        for (var i = 0; i < 6; i++)
            near.Add(await WorkerAsync($"Near {i}", North(at, 2 + i * 5)));
        var offline = await WorkerAsync("Offline Nearby", North(at, 1), available: false);
        var wrongSkill = await WorkerAsync("Nearby Plumber", North(at, 1), skill: "construction");
        var far = await WorkerAsync("Far Away", North(at, 80));

        await using var hub = await app.ConnectAsync(near[0]);
        var offer = Wait.ForEventAsync<TopicMessageDto>(hub, nameof(IChatClient.TopicMessage), m => m.Topic == GigsContract.UserTopic(near[0].Id));
        await hub.InvokeAsync(ChatHubContract.Subscribe, GigsContract.UserTopic(near[0].Id));

        var client = await app.CreateUserAsync("Wedding Client");
        var gig = await client.PostAsync<GigDto>(Base, Shoot(at));
        Assert.Equal((GigStatuses.Open, "25.5", "NMX"), (gig.Status, gig.Budget, gig.TokenSymbol));

        var news = ContractJson.Read<GigNewsDto>((await offer).Payload)!;
        Assert.Equal(("offer", gig.Id), (news.Kind, news.Gig.Id));
        Assert.Equal(GigsContract.Approximate(at.Lat), news.Gig.Latitude);
        Assert.Equal(2, news.Gig.DistanceKm!.Value, 0);

        var offered = await app.DbAsync(db => db.GigOffers.Where(o => o.GigId == gig.Id).Select(o => o.WorkerId).ToListAsync());
        Assert.Equal(near.Take(GigsContract.DispatchFanOut).Select(w => w.Id).Order(), offered.Order());

        // Everyone else can find it on the board, at its rough area only.
        foreach (var outsider in new[] { near[5], offline, wrongSkill, far })
        {
            var seen = await outsider.GetAsync<GigDto>($"{Base}/{gig.Id}");
            Assert.Equal((GigsContract.Approximate(at.Lat), (string?)null), (seen.Latitude, seen.MyOffer));
        }

        // Someone else's news isn't followable.
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(ChatHubContract.Subscribe, GigsContract.UserTopic(client.Id)));
    }

    [Fact]
    public async Task One_worker_gets_the_gig_and_a_chat_with_the_client()
    {
        var at = Somewhere();
        var a = await WorkerAsync("Racer A", North(at, 1));
        var b = await WorkerAsync("Racer B", North(at, 2));
        var client = await app.CreateUserAsync("Race Client");
        var gig = await client.PostAsync<GigDto>(Base, Shoot(at));

        var attempts = await Task.WhenAll(new[] { a, b }.Select(w => w.Http.PostAsJsonAsync($"{Base}/{gig.Id}/accept", new { }, ContractJson.Options)));
        Assert.Single(attempts, r => r.IsSuccessStatusCode);
        var loser = attempts.Single(r => !r.IsSuccessStatusCode);
        Assert.Contains("Someone else took", await loser.Content.ReadAsStringAsync());

        var taken = await client.GetAsync<GigDto>($"{Base}/{gig.Id}");
        Assert.Equal(GigStatuses.Assigned, taken.Status);
        var worker = taken.Worker!.Id == a.Id ? a : b;
        var seenByWorker = await worker.GetAsync<GigDto>($"{Base}/{gig.Id}");
        Assert.Equal((at.Lat, taken.ConversationId), (seenByWorker.Latitude, seenByWorker.ConversationId));

        var thread = await client.GetAsync<List<MessageDto>>($"api/conversations/{taken.ConversationId}/messages");
        Assert.Contains("I've taken your gig", ContractJson.Read<TextPayload>(Assert.Single(thread).Payload)!.Text);
        foreach (var r in attempts)
            r.Dispose();
    }

    [Fact]
    public async Task Approving_pays_the_worker_in_ndeipicoin_once_and_both_sides_rate()
    {
        var at = Somewhere();
        var worker = await WorkerAsync("Paid Worker", North(at, 3));
        var client = await app.CreateUserAsync("Paying Client");
        var gig = await client.PostAsync<GigDto>(Base, Shoot(at, budget: "40"));
        await worker.PostAsync<GigDto>($"{Base}/{gig.Id}/accept", new { });

        using (var early = await worker.Http.PostAsJsonAsync($"{Base}/{gig.Id}/approve", new { }, ContractJson.Options))
            Assert.Equal(HttpStatusCode.NotFound, early.StatusCode);
        Assert.Equal(GigStatuses.Submitted, (await worker.PostAsync<GigDto>($"{Base}/{gig.Id}/submit", new { })).Status);

        var done = await client.PostAsync<GigDto>($"{Base}/{gig.Id}/approve", new { });
        Assert.Equal((GigStatuses.Completed, TransferStatuses.Pending), (done.Status, done.PaymentStatus));
        await client.PostAsync<GigDto>($"{Base}/{gig.Id}/approve", new { });

        var payments = await app.DbAsync(db => db.TokenTransfers.AsNoTracking().Where(t => t.ConversationId == done.ConversationId).ToListAsync());
        var payment = Assert.Single(payments);
        Assert.Equal((client.Id, worker.Id, 40m, "NMX"), (payment.SenderUserId, payment.RecipientUserId, payment.Amount, payment.TokenSymbol));

        await client.PostAsync<GigDto>($"{Base}/{gig.Id}/rating", new RateGigRequest(4));
        await worker.PostAsync<GigDto>($"{Base}/{gig.Id}/rating", new RateGigRequest(5));
        using (var twice = await client.Http.PostAsJsonAsync($"{Base}/{gig.Id}/rating", new RateGigRequest(1), ContractJson.Options))
            Assert.Equal(HttpStatusCode.BadRequest, twice.StatusCode);

        var profile = await worker.GetAsync<GigProfileDto>($"{Base}/profile");
        Assert.Equal((1, 4.0, 1), (profile.CompletedGigs, profile.Rating, profile.RatingCount));
        var final = await client.GetAsync<GigDto>($"{Base}/{gig.Id}");
        Assert.Equal((4, 5), (final.WorkerStars, final.ClientStars));
    }

    [Fact]
    public async Task A_gig_started_in_a_direct_chat_goes_to_the_other_person()
    {
        var at = Somewhere();
        var bystander = await WorkerAsync("Chat Bystander", North(at, 1));
        var friend = await WorkerAsync("Chat Friend", North(at, 30));
        var client = await app.CreateUserAsync("Chat Client");
        var chat = await client.StartDirectChatAsync(friend);

        var gig = await client.PostAsync<GigDto>(Base, Shoot(at, conversationId: chat.Id));
        var offers = await app.DbAsync(db => db.GigOffers.Where(o => o.GigId == gig.Id).Select(o => o.WorkerId).ToListAsync());
        Assert.Equal([friend.Id], offers);

        var taken = await friend.PostAsync<GigDto>($"{Base}/{gig.Id}/accept", new { });
        Assert.Equal(chat.Id, taken.ConversationId);

        // Someone who hasn't set up to work can't be hired from a chat.
        var nonWorker = await app.CreateUserAsync("Not A Worker");
        var other = await client.StartDirectChatAsync(nonWorker);
        using var refused = await client.Http.PostAsJsonAsync(Base, Shoot(at, conversationId: other.Id), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Gigs profile", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Going_available_brings_offers_for_open_gigs_nearby_and_declines_move_on()
    {
        var at = Somewhere();
        var client = await app.CreateUserAsync("Waiting Client");
        var gig = await client.PostAsync<GigDto>(Base, Shoot(at));
        Assert.Equal(0, await app.DbAsync(db => db.GigOffers.CountAsync(o => o.GigId == gig.Id)));

        var late = await WorkerAsync("Late Worker", North(at, 4));
        var mine = await late.GetAsync<List<GigDto>>(Base);
        Assert.Equal(GigOfferStatuses.Offered, mine.Single(g => g.Id == gig.Id).MyOffer);

        var later = await WorkerAsync("Later Worker", North(at, 9), available: false);
        await late.PostAsync<GigDto>($"{Base}/{gig.Id}/decline", new { });
        await Put<GigProfileDto>(later, $"{Base}/profile/availability", new AvailabilityRequest(true));
        Assert.Equal(GigOfferStatuses.Offered, (await later.GetAsync<GigDto>($"{Base}/{gig.Id}")).MyOffer);

        // Cancelling closes the remaining offers.
        Assert.Equal(GigStatuses.Cancelled, (await client.PostAsync<GigDto>($"{Base}/{gig.Id}/cancel", new { })).Status);
        using var gone = await later.Http.PostAsJsonAsync($"{Base}/{gig.Id}/accept", new { }, ContractJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }
}
