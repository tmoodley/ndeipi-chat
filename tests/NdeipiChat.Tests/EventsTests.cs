using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Events;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class EventsTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = EventsContract.BasePath;

    static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    Task<TestUser> OrganizerAsync(string name) => app.CreateUserAsync(name, $"{Guid.NewGuid():N}@example.test", roles: [EventsContract.OrganizerRole]);

    async Task VerifyBankingAsync(TestUser user) => await app.DbAsync(db =>
    {
        db.BankingProfiles.Add(new BankingProfile
        {
            UserId = user.Id,
            BridgeCustomerId = NewId("cust"),
            KycLinkId = NewId("kyc"),
            KycStatus = BankingProfile.Approved,
            TosStatus = BankingProfile.Approved,
            WalletId = NewId("wal"),
            WalletChain = "solana",
            WalletAddress = NewId("addr"),
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return db.SaveChangesAsync();
    });

    static SaveEventRequest Concert(params SaveTierRequest[] tiers) => new(
        "Jah Prayzah Live", "An evening at HICC.", EventCategories.Music, "Harare", "HICC",
        DateTimeOffset.UtcNow.AddDays(14), tiers.Length > 0 ? tiers : [new SaveTierRequest(null, "General", "0", 100)]);

    async Task<EventDetailDto> PublishedAsync(TestUser organizer, SaveEventRequest request)
    {
        var created = await organizer.PostAsync<EventDetailDto>(Base, request);
        return await organizer.PostAsync<EventDetailDto>($"{Base}/{created.Id}/publish", new { });
    }

    static async Task<string> ErrorOf(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    [Fact]
    public async Task Only_organizers_create_events_and_drafts_stay_private()
    {
        var fan = await app.CreateUserAsync("Events Fan");
        using var refused = await fan.Http.PostAsJsonAsync(Base, Concert(), ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("organizers", await ErrorOf(refused));

        var organizer = await OrganizerAsync("Draft Organizer");
        var draft = await organizer.PostAsync<EventDetailDto>(Base, Concert());
        Assert.False(draft.IsPublished);
        Assert.True(draft.CanManage);

        using var hidden = await fan.Http.GetAsync($"{Base}/{draft.Id}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        await organizer.PostAsync<EventDetailDto>($"{Base}/{draft.Id}/publish", new { });
        var seen = await fan.GetAsync<EventDetailDto>($"{Base}/{draft.Id}");
        Assert.False(seen.CanManage);
        Assert.Equal(100, seen.Tiers.Single().Available);
    }

    [Fact]
    public async Task The_catalog_filters_by_city_and_category()
    {
        var organizer = await OrganizerAsync("Catalog Organizer");
        var city = "Mutare" + Guid.NewGuid().ToString("N")[..6];
        var gig = await PublishedAsync(organizer, Concert() with { City = city });
        var summit = await PublishedAsync(organizer, Concert() with { City = city, Category = EventCategories.Business, Title = "Mining Indaba" });

        var fan = await app.CreateUserAsync("Catalog Fan");
        var all = await fan.GetAsync<EventPageDto>($"{Base}?city={city}");
        Assert.Equal(2, all.Total);
        Assert.Equal([gig.Id, summit.Id], all.Events.Select(e => e.Id));
        Assert.Contains(city, all.Cities);

        var business = await fan.GetAsync<EventPageDto>($"{Base}?city={city}&category=business");
        Assert.Equal(summit.Id, Assert.Single(business.Events).Id);
        Assert.Equal("0", business.Events[0].LowestPrice);
    }

    [Fact]
    public async Task Holds_never_oversell_even_when_buyers_race()
    {
        var organizer = await OrganizerAsync("Race Organizer");
        var e = await PublishedAsync(organizer, Concert(new SaveTierRequest(null, "Golden Circle", "0", 5)));
        var tier = e.Tiers.Single();

        var buyers = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => app.CreateUserAsync($"Racer {i}")));
        var attempts = await Task.WhenAll(buyers.Select(b => b.Http.PostAsJsonAsync($"{Base}/{e.Id}/holds", new HoldRequest(tier.Id, 1), ContractJson.Options)));
        Assert.Equal(5, attempts.Count(r => r.IsSuccessStatusCode));
        Assert.All(attempts.Where(r => !r.IsSuccessStatusCode), r => Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode));

        var after = await organizer.GetAsync<EventDetailDto>($"{Base}/{e.Id}");
        Assert.Equal(0, after.Tiers.Single().Available);
        foreach (var r in attempts)
            r.Dispose();
    }

    [Fact]
    public async Task Expired_and_released_holds_go_back_on_sale_and_availability_is_broadcast()
    {
        var organizer = await OrganizerAsync("Sweep Organizer");
        var e = await PublishedAsync(organizer, Concert(new SaveTierRequest(null, "VIP", "0", 4)));
        var tier = e.Tiers.Single();

        var watcher = await app.CreateUserAsync("Availability Watcher");
        await using var hub = await app.ConnectAsync(watcher);
        await hub.InvokeAsync(ChatHubContract.Subscribe, EventsContract.Topic(e.Id));

        var buyer = await app.CreateUserAsync("Sweep Buyer");
        var held = Wait.ForEventAsync<TopicMessageDto>(hub, nameof(IChatClient.TopicMessage),
            m => ContractJson.Read<EventAvailabilityDto>(m.Payload)!.Tiers.Single().Available == 1);
        var hold = await buyer.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(tier.Id, 3));
        Assert.Equal((await held).Topic, EventsContract.Topic(e.Id));

        // Ten minutes pass.
        await app.DbAsync(db => db.TicketHolds.Where(h => h.Id == hold.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1))));
        var freed = Wait.ForEventAsync<TopicMessageDto>(hub, nameof(IChatClient.TopicMessage),
            m => ContractJson.Read<EventAvailabilityDto>(m.Payload)!.Tiers.Single().Available == 4);
        Assert.True(await TicketHoldSweeper.SweepAsync(app.Services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, default) >= 1);
        await freed;

        using var late = await buyer.Http.PostAsync($"{Base}/holds/{hold.Id}/pay", null);
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Contains("expired", await ErrorOf(late));

        // A new hold, given back by hand.
        var again = await buyer.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(tier.Id, 2));
        using var released = await buyer.Http.DeleteAsync($"{Base}/holds/{again.Id}");
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
        Assert.Equal(4, (await buyer.GetAsync<EventDetailDto>($"{Base}/{e.Id}")).Tiers.Single().Available);
    }

    [Fact]
    public async Task Drafts_cannot_be_followed()
    {
        var organizer = await OrganizerAsync("Topic Organizer");
        var draft = await organizer.PostAsync<EventDetailDto>(Base, Concert());
        var stranger = await app.CreateUserAsync("Topic Stranger");
        await using var hub = await app.ConnectAsync(stranger);
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(ChatHubContract.Subscribe, EventsContract.Topic(draft.Id)));
        await Assert.ThrowsAsync<HubException>(() => hub.InvokeAsync(ChatHubContract.Subscribe, "nobody:abc"));
    }

    [Fact]
    public async Task Paid_tickets_are_issued_when_the_wallet_transfer_is_confirmed()
    {
        var organizer = await OrganizerAsync("Paid Organizer");
        var buyer = await app.CreateUserAsync("Paid Buyer");
        await VerifyBankingAsync(organizer);
        await VerifyBankingAsync(buyer);
        var e = await PublishedAsync(organizer, Concert(new SaveTierRequest(null, "General", "12.50", 50)));
        var tier = e.Tiers.Single();

        var bridgeTransferId = NewId("tr");
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { id = bridgeTransferId, state = "awaiting_funds" });

        var hold = await buyer.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(tier.Id, 2));
        Assert.Equal("25", hold.Amount);
        var order = await buyer.PostAsync<OrderDto>($"{Base}/holds/{hold.Id}/pay", new { });
        Assert.Equal(OrderStatuses.Pending, order.Status);
        Assert.Empty(order.Tickets);

        var transfer = await app.DbAsync(db => db.BankTransfers.AsNoTracking().SingleAsync(t => t.OrderId == order.Id));
        Assert.Equal((buyer.Id, organizer.Id, 25m), (transfer.SenderId, transfer.RecipientId, transfer.Amount));

        using var webhook = await DeliverTransferWebhookAsync(bridgeTransferId, "payment_processed");
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);

        var paid = await buyer.GetAsync<OrderDto>($"{Base}/orders/{order.Id}");
        Assert.Equal(OrderStatuses.Paid, paid.Status);
        Assert.Equal(2, paid.Tickets.Count);
        var detail = await buyer.GetAsync<EventDetailDto>($"{Base}/{e.Id}");
        Assert.Equal(48, detail.Tiers.Single().Available);
        var tiers = await app.DbAsync(db => db.TicketTiers.AsNoTracking().SingleAsync(t => t.Id == tier.Id));
        Assert.Equal((2, 0), (tiers.Sold, tiers.Held));

        // A repeated webhook doesn't issue twice.
        using var again = await DeliverTransferWebhookAsync(bridgeTransferId, "payment_processed");
        Assert.Equal(2, (await buyer.GetAsync<List<TicketDto>>($"{Base}/tickets")).Count(t => t.EventId == e.Id));
    }

    [Fact]
    public async Task A_failed_payment_frees_the_seats()
    {
        var organizer = await OrganizerAsync("Declined Organizer");
        var buyer = await app.CreateUserAsync("Declined Buyer");
        await VerifyBankingAsync(organizer);
        await VerifyBankingAsync(buyer);
        var e = await PublishedAsync(organizer, Concert(new SaveTierRequest(null, "General", "5", 3)));
        var tier = e.Tiers.Single();

        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { code = "insufficient_funds", message = "Insufficient funds" }, HttpStatusCode.BadRequest);
        var hold = await buyer.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(tier.Id, 3));
        var order = await buyer.PostAsync<OrderDto>($"{Base}/holds/{hold.Id}/pay", new { });

        Assert.Equal(OrderStatuses.Failed, order.Status);
        Assert.NotNull(order.Error);
        Assert.Equal(3, (await buyer.GetAsync<EventDetailDto>($"{Base}/{e.Id}")).Tiers.Single().Available);
    }

    [Fact]
    public async Task Paid_tickets_need_verified_wallets()
    {
        var organizer = await OrganizerAsync("Unverified Organizer");
        var buyer = await app.CreateUserAsync("Unverified Buyer");
        var e = await PublishedAsync(organizer, Concert(new SaveTierRequest(null, "General", "5", 3)));
        var hold = await buyer.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(e.Tiers.Single().Id, 1));

        using var refused = await buyer.Http.PostAsync($"{Base}/holds/{hold.Id}/pay", null);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Verify your identity", await ErrorOf(refused));

        // The hold is still there to pay once they've verified.
        var stored = await app.DbAsync(db => db.TicketHolds.AsNoTracking().SingleAsync(h => h.Id == hold.Id));
        Assert.Equal(HoldStatuses.Active, stored.Status);
    }

    [Fact]
    public async Task Rolling_codes_signed_on_the_holders_device_admit_once_at_the_gate()
    {
        var organizer = await OrganizerAsync("Gate Organizer");
        var e = await PublishedAsync(organizer, Concert());
        var fan = await app.CreateUserAsync("Gate Fan");
        var hold = await fan.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(e.Tiers.Single().Id, 1));
        var order = await fan.PostAsync<OrderDto>($"{Base}/holds/{hold.Id}/pay", new { });
        var ticket = Assert.Single(order.Tickets);
        Assert.Equal(OrderStatuses.Paid, order.Status);
        Assert.Null(ticket.HolderPublicKey);

        // The fan's phone makes a key and registers its public half.
        using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var bound = await fan.Http.PostAsJsonAsync($"{Base}/tickets/{ticket.Id}/holder-key",
            new BindHolderKeyRequest(Convert.ToBase64String(device.ExportSubjectPublicKeyInfo())), ContractJson.Options);
        Assert.Equal(HttpStatusCode.NoContent, bound.StatusCode);

        // Someone else can't bind their key to it.
        var thief = await app.CreateUserAsync("Gate Thief");
        using var stolen = await thief.Http.PostAsJsonAsync($"{Base}/tickets/{ticket.Id}/holder-key",
            new BindHolderKeyRequest(Convert.ToBase64String(device.ExportSubjectPublicKeyInfo())), ContractJson.Options);
        Assert.Equal(HttpStatusCode.NotFound, stolen.StatusCode);

        // Only the organizer and their validators get the gate list.
        using var denied = await thief.Http.GetAsync($"{Base}/{e.Id}/gate");
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        var validator = await app.CreateUserAsync("Gate Validator", $"{Guid.NewGuid():N}@example.test");
        await organizer.PostAsync<UserDto>($"{Base}/{e.Id}/validators", new AddValidatorRequest(await EmailOf(validator)));
        var gate = await validator.GetAsync<GateListDto>($"{Base}/{e.Id}/gate");
        var entry = gate.Tickets.Single(t => t.TicketId == ticket.Id);
        Assert.Equal("Gate Fan", entry.HolderName);

        // The code on screen verifies against the gate list alone -- no network needed.
        var window = EventsContract.WindowAt(DateTimeOffset.UtcNow);
        var signature = device.SignData(EventsContract.QrPayload(ticket.Id, window), HashAlgorithmName.SHA256);
        var qr = EventsContract.QrText(ticket.Id, window, Convert.ToBase64String(signature));
        Assert.True(EventsContract.TryParseQr(qr, out var scannedId, out var scannedWindow, out var scannedSig));
        using (var gateKey = ECDsa.Create())
        {
            gateKey.ImportSubjectPublicKeyInfo(Convert.FromBase64String(entry.HolderPublicKey!), out _);
            Assert.True(gateKey.VerifyData(EventsContract.QrPayload(scannedId, scannedWindow), Convert.FromBase64String(scannedSig), HashAlgorithmName.SHA256));
            Assert.False(gateKey.VerifyData(EventsContract.QrPayload(scannedId, scannedWindow + 1), Convert.FromBase64String(scannedSig), HashAlgorithmName.SHA256));
        }

        // Two gates admit the same ticket offline; the first upload wins.
        var first = DateTimeOffset.UtcNow.AddMinutes(-2);
        var results = await validator.PostAsync<List<AdmissionResultDto>>($"{Base}/{e.Id}/admissions",
            new AdmissionsRequest([new AdmissionDto(ticket.Id, first)], "North gate"));
        Assert.Equal(AdmissionOutcomes.Admitted, Assert.Single(results).Outcome);
        var second = await organizer.PostAsync<List<AdmissionResultDto>>($"{Base}/{e.Id}/admissions",
            new AdmissionsRequest([new AdmissionDto(ticket.Id, DateTimeOffset.UtcNow), new AdmissionDto(Guid.NewGuid(), DateTimeOffset.UtcNow)], "South gate"));
        Assert.Equal(AdmissionOutcomes.AlreadyAdmitted, second[0].Outcome);
        Assert.Equal(first.ToUnixTimeSeconds(), second[0].FirstAdmittedAt!.Value.ToUnixTimeSeconds());
        Assert.Equal(AdmissionOutcomes.Unknown, second[1].Outcome);

        Assert.Equal(TicketStatuses.Admitted, (await fan.GetAsync<List<TicketDto>>($"{Base}/tickets")).Single(t => t.Id == ticket.Id).Status);
    }

    [Fact]
    public async Task Tiers_cannot_shrink_below_what_is_sold()
    {
        var organizer = await OrganizerAsync("Shrink Organizer");
        var e = await PublishedAsync(organizer, Concert(new SaveTierRequest(null, "General", "0", 10)));
        var fan = await app.CreateUserAsync("Shrink Fan");
        var hold = await fan.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(e.Tiers.Single().Id, 4));
        await fan.PostAsync<OrderDto>($"{Base}/holds/{hold.Id}/pay", new { });

        var tier = e.Tiers.Single();
        using var shrunk = await organizer.Http.PutAsJsonAsync($"{Base}/{e.Id}",
            Concert(new SaveTierRequest(tier.Id, tier.Name, "0", 3)) with { StartsAt = e.StartsAt }, ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, shrunk.StatusCode);

        using var removed = await organizer.Http.PutAsJsonAsync($"{Base}/{e.Id}",
            Concert(new SaveTierRequest(null, "Other", "0", 3)) with { StartsAt = e.StartsAt }, ContractJson.Options);
        Assert.Equal(HttpStatusCode.BadRequest, removed.StatusCode);
    }

    Task<string> EmailOf(TestUser user) => app.DbAsync(db => db.Users.Where(u => u.Id == user.Id).Select(u => u.Email!).SingleAsync());

    async Task<HttpResponseMessage> DeliverTransferWebhookAsync(string bridgeTransferId, string state)
    {
        var body = JsonSerializer.Serialize(new
        {
            event_id = NewId("wh"),
            event_category = "transfer",
            event_type = "transfer.updated.status_transitioned",
            event_object_id = bridgeTransferId,
            event_object_status = state,
            event_object = new { id = bridgeTransferId, state }
        });
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        var signature = Convert.ToBase64String(TestApp.WebhookKey.SignData(
            Encoding.UTF8.GetBytes($"{timestamp}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/bridge") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Webhook-Signature", $"t={timestamp},v0={signature}");
        return await app.CreateClient().SendAsync(request);
    }
}
