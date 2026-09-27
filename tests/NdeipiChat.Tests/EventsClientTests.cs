using System.Net.Http.Json;
using Microsoft.AspNetCore.SignalR;
using NdeipiChat.Client.Livestock;
using NdeipiChat.Contracts;
using NdeipiChat.SubApps.Events;
using NdeipiChat.SubApps.Sdk;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>The Events sub-app's client side: the shell's topics, and the gate's offline checks.</summary>
public sealed class EventsClientTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = EventsContract.BasePath;

    /// <summary>What the web shell's ShellDevice does, over .NET's ECDsa and a dictionary.</summary>
    sealed class MemoryDevice(string name = "Test gate") : ISubAppDevice
    {
        readonly DotNetP256Signer _signer = new();
        readonly Dictionary<string, (string Public, string Private)> _keys = [];
        public Dictionary<string, string> Values { get; } = [];

        public string DeviceName => name;

        public async Task<string> CreateKeyAsync(string keyName)
        {
            var (pub, priv) = await _signer.CreateKeyAsync();
            _keys[keyName] = (pub, priv);
            return pub;
        }

        public Task<string?> GetPublicKeyAsync(string keyName) => Task.FromResult(_keys.TryGetValue(keyName, out var k) ? k.Public : null);

        public async Task<string?> SignAsync(string keyName, byte[] payload) =>
            _keys.TryGetValue(keyName, out var k) ? await _signer.SignAsync(k.Private, payload) : null;

        public Task<bool> VerifyAsync(string publicKeySpki, byte[] payload, string signature) => _signer.VerifyAsync(publicKeySpki, payload, signature);

        public Task<string?> GetValueAsync(string valueName) => Task.FromResult(Values.GetValueOrDefault(valueName));

        public Task SetValueAsync(string valueName, string? value)
        {
            if (value is null)
                Values.Remove(valueName);
            else
                Values[valueName] = value;
            return Task.CompletedTask;
        }
    }

    async Task<(TestUser Organizer, EventDetailDto Event)> EventAsync(int seats = 10)
    {
        var organizer = await app.CreateUserAsync("Client Organizer", $"{Guid.NewGuid():N}@example.test", roles: [EventsContract.OrganizerRole]);
        var created = await organizer.PostAsync<EventDetailDto>(Base, new SaveEventRequest(
            "Gate Night", null, EventCategories.Comedy, "Bulawayo", "City Hall", DateTimeOffset.UtcNow.AddDays(3),
            [new SaveTierRequest(null, "General", "0", seats)]));
        return (organizer, await organizer.PostAsync<EventDetailDto>($"{Base}/{created.Id}/publish", new { }));
    }

    async Task<(TicketDto Ticket, MemoryDevice Phone)> TicketAsync(EventDetailDto e, string holder, bool open = true)
    {
        var fan = await app.CreateUserAsync(holder);
        var hold = await fan.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(e.Tiers.Single().Id, 1));
        var ticket = (await fan.PostAsync<OrderDto>($"{Base}/holds/{hold.Id}/pay", new { })).Tickets.Single();
        var phone = new MemoryDevice();
        if (!open)
            return (ticket, phone);
        var key = await phone.CreateKeyAsync($"ticket.{ticket.Id:N}");
        using var bound = await fan.Http.PostAsJsonAsync($"{Base}/tickets/{ticket.Id}/holder-key", new BindHolderKeyRequest(key), ContractJson.Options);
        bound.EnsureSuccessStatusCode();
        return (ticket, phone);
    }

    static async Task<string> CodeAsync(MemoryDevice phone, Guid ticketId, DateTimeOffset at)
    {
        var window = EventsContract.WindowAt(at);
        var signature = await phone.SignAsync($"ticket.{ticketId:N}", EventsContract.QrPayload(ticketId, window));
        return EventsContract.QrText(ticketId, window, signature!);
    }

    [Fact]
    public async Task The_shell_connection_delivers_topic_messages_until_unsubscribed()
    {
        var (_, e) = await EventAsync();
        var viewer = await app.CreateUserAsync("Client Viewer");
        await using var client = await ClientHarness.SignInAsync(app, viewer);
        var connection = client.Session.Connection;

        var received = new List<TopicMessageDto>();
        connection.TopicMessageReceived += m => { lock (received) received.Add(m); };
        await connection.SubscribeAsync(EventsContract.Topic(e.Id));

        var buyer = await app.CreateUserAsync("Client Buyer");
        await buyer.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(e.Tiers.Single().Id, 2));
        await Wait.UntilAsync(() => { lock (received) return received.Count > 0; }, "the hold is broadcast");
        var update = ContractJson.Read<EventAvailabilityDto>(received[0].Payload)!;
        Assert.Equal(8, update.Tiers.Single().Available);

        await connection.UnsubscribeAsync(EventsContract.Topic(e.Id));
        lock (received) received.Clear();
        await buyer.PostAsync<HoldDto>($"{Base}/{e.Id}/holds", new HoldRequest(e.Tiers.Single().Id, 1));
        await Task.Delay(500);
        lock (received) Assert.Empty(received);

        // The server's refusal reaches the caller.
        await Assert.ThrowsAsync<HubException>(() => connection.SubscribeAsync(EventsContract.Topic(Guid.NewGuid())));
    }

    [Fact]
    public async Task A_gate_admits_offline_and_refuses_stale_forged_and_repeated_codes()
    {
        var (organizer, e) = await EventAsync();
        var (ticket, phone) = await TicketAsync(e, "Offline Fan");
        var (unopened, _) = await TicketAsync(e, "Other Fan", open: false);

        var gateDevice = new MemoryDevice("North gate");
        var gate = new GateBook(e.Id, gateDevice);
        await gate.ApplyListAsync(await organizer.GetAsync<GateListDto>($"{Base}/{e.Id}/gate"));

        // From here on, no network: everything below runs on the gate's copy.
        var now = DateTimeOffset.UtcNow;
        Assert.Equal(GateVerdict.NotATicket, (await gate.CheckAsync("hello", now)).Verdict);
        Assert.Equal(GateVerdict.Unknown, (await gate.CheckAsync(EventsContract.QrText(Guid.NewGuid(), EventsContract.WindowAt(now), "AAAA"), now)).Verdict);
        Assert.Equal(GateVerdict.NotActivated, (await gate.CheckAsync(EventsContract.QrText(unopened.Id, EventsContract.WindowAt(now), "AAAA"), now)).Verdict);
        Assert.Equal(GateVerdict.Expired, (await gate.CheckAsync(await CodeAsync(phone, ticket.Id, now.AddMinutes(-2)), now)).Verdict);

        var impostor = new MemoryDevice();
        await impostor.CreateKeyAsync($"ticket.{ticket.Id:N}");
        Assert.Equal(GateVerdict.Forged, (await gate.CheckAsync(await CodeAsync(impostor, ticket.Id, now), now)).Verdict);

        // A clock a window or two off is fine.
        var admitted = await gate.CheckAsync(await CodeAsync(phone, ticket.Id, now.AddSeconds(-EventsContract.QrWindowSeconds)), now);
        Assert.Equal((GateVerdict.Admit, "General · Offline Fan"), (admitted.Verdict, admitted.Message));
        Assert.Equal(GateVerdict.AlreadyAdmitted, (await gate.CheckAsync(await CodeAsync(phone, ticket.Id, now), now.AddSeconds(5))).Verdict);

        // The gate restarts (e.g. the tab reloads) and still remembers.
        var reopened = new GateBook(e.Id, gateDevice);
        await reopened.LoadAsync();
        Assert.Equal(GateVerdict.AlreadyAdmitted, (await reopened.CheckAsync(await CodeAsync(phone, ticket.Id, now), now)).Verdict);
        Assert.Single(reopened.Pending);

        // Meanwhile another gate let the same person in and uploaded first.
        var south = new GateBook(e.Id, new MemoryDevice("South gate"));
        await south.ApplyListAsync(await organizer.GetAsync<GateListDto>($"{Base}/{e.Id}/gate"));
        Assert.Equal(GateVerdict.Admit, (await south.CheckAsync(await CodeAsync(phone, ticket.Id, now), now.AddSeconds(-30))).Verdict);
        Assert.Empty(await south.UploadAsync(async r => await organizer.PostAsync<List<AdmissionResultDto>>($"{Base}/{e.Id}/admissions", r)));

        // Back online, the north gate uploads and learns it was second.
        var conflicts = await reopened.UploadAsync(async r => await organizer.PostAsync<List<AdmissionResultDto>>($"{Base}/{e.Id}/admissions", r));
        Assert.Equal(AdmissionOutcomes.AlreadyAdmitted, Assert.Single(conflicts).Outcome);
        Assert.Empty(reopened.Pending);

        var fresh = await organizer.GetAsync<GateListDto>($"{Base}/{e.Id}/gate");
        Assert.Equal(TicketStatuses.Admitted, fresh.Tickets.Single(t => t.TicketId == ticket.Id).Status);
    }
}
