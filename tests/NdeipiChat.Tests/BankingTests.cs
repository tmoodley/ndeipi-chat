using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR.Client;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

public sealed class BankingTests(TestApp app) : IClassFixture<TestApp>
{
    sealed record VerifiedUser(TestUser User, string CustomerId, string WalletId, string Address);

    static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    static BankTransferState StateOf(JsonElement state) => ContractJson.Read<BankTransferState>(state)!;

    async Task<VerifiedUser> VerifiedUserAsync(string name)
    {
        var user = await app.CreateUserAsync(name);
        var verified = new VerifiedUser(user, NewId("cust"), NewId("wal"), NewId("addr"));
        await app.DbAsync(db =>
        {
            db.BankingProfiles.Add(new BankingProfile
            {
                UserId = user.Id,
                BridgeCustomerId = verified.CustomerId,
                KycLinkId = NewId("kyc"),
                KycStatus = BankingProfile.Approved,
                TosStatus = BankingProfile.Approved,
                WalletId = verified.WalletId,
                WalletChain = "solana",
                WalletAddress = verified.Address,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            return db.SaveChangesAsync();
        });
        return verified;
    }

    async Task<HttpResponseMessage> DeliverWebhookAsync(object evt, DateTimeOffset? sentAt = null, string? bodyOverride = null, bool sign = true)
    {
        var body = JsonSerializer.Serialize(evt);
        var timestamp = (sentAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds().ToString();
        var signature = Convert.ToBase64String(TestApp.WebhookKey.SignData(
            Encoding.UTF8.GetBytes($"{timestamp}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/bridge")
        {
            Content = new StringContent(bodyOverride ?? body, Encoding.UTF8, "application/json")
        };
        if (sign)
            request.Headers.Add("X-Webhook-Signature", $"t={timestamp},v0={signature}");
        return await app.CreateClient().SendAsync(request);
    }

    static object KycEvent(string linkId, string customerId, string kycStatus, string tosStatus) => new
    {
        api_version = "v0",
        event_id = NewId("wh"),
        event_category = "kyc_link",
        event_type = "kyc_link.updated.status_transitioned",
        event_object_id = linkId,
        event_object_status = kycStatus,
        event_object = new { id = linkId, kyc_status = kycStatus, tos_status = tosStatus, customer_id = customerId },
        event_created_at = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task A_kyc_link_is_created_once_then_reused()
    {
        var user = await app.CreateUserAsync("Tendai Ncube", "tendai@example.test");
        var linkId = NewId("kyc");
        var link = new
        {
            id = linkId,
            kyc_link = "https://bridge.test/verify/abc",
            tos_link = "https://bridge.test/tos/abc",
            kyc_status = "not_started",
            tos_status = "pending",
            customer_id = NewId("cust")
        };
        app.Bridge.OnJson(HttpMethod.Post, "/v0/kyc_links", link);
        app.Bridge.OnJson(HttpMethod.Get, $"/v0/kyc_links/{linkId}", link);

        var first = await user.PostAsync<KycLinkDto>("api/banking/kyc", new { });
        Assert.Equal("https://bridge.test/tos/abc", first.TosUrl);
        Assert.Equal("https://bridge.test/verify/abc", first.KycUrl);
        Assert.Equal(("not_started", "pending", false, "usdc"), (first.Status.KycStatus, first.Status.TosStatus, first.Status.CanTransfer, first.Status.Currency));

        var create = Assert.Single(app.Bridge.RequestsTo(HttpMethod.Post, "/v0/kyc_links"), r => r.Body!.Contains("tendai@example.test"));
        Assert.Equal("Tendai Ncube", create.Json.GetProperty("full_name").GetString());
        Assert.Equal("individual", create.Json.GetProperty("type").GetString());
        Assert.Equal("bridge-test-key", create.ApiKey);
        Assert.Equal($"kyc-link-{user.Id}", create.IdempotencyKey);

        var again = await user.PostAsync<KycLinkDto>("api/banking/kyc", new { });
        Assert.Equal(first.KycUrl, again.KycUrl);
        Assert.Single(app.Bridge.RequestsTo(HttpMethod.Post, "/v0/kyc_links"), r => r.Body!.Contains("tendai@example.test"));
        Assert.NotEmpty(app.Bridge.RequestsTo(HttpMethod.Get, $"/v0/kyc_links/{linkId}"));
    }

    [Fact]
    public async Task Approval_by_webhook_opens_a_wallet_and_tells_the_user()
    {
        var user = await app.CreateUserAsync("Nyasha Approved");
        var linkId = NewId("kyc");
        var customerId = NewId("cust");
        app.Bridge.OnJson(HttpMethod.Post, "/v0/kyc_links", new
        {
            id = linkId, kyc_link = "https://bridge.test/k", tos_link = "https://bridge.test/t",
            kyc_status = "not_started", tos_status = "pending", customer_id = customerId
        });
        app.Bridge.OnJson(HttpMethod.Post, $"/v0/customers/{customerId}/wallets", new { id = "wal_approved", chain = "solana", address = "So1anaAddress" });
        await user.PostAsync<KycLinkDto>("api/banking/kyc", new { });

        await using var hub = await app.ConnectAsync(user);
        var ready = Wait.ForEventAsync<BankingStatusDto>(hub, nameof(IChatClient.BankingStatusChanged), s => s.CanTransfer);

        var approved = KycEvent(linkId, customerId, "approved", "approved");
        using (var response = await DeliverWebhookAsync(approved))
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var status = await ready;
        Assert.Equal(("solana", "So1anaAddress"), (status.WalletChain, status.WalletAddress));
        var createWallet = Assert.Single(app.Bridge.RequestsTo(HttpMethod.Post, $"/v0/customers/{customerId}/wallets"));
        Assert.Equal("solana", createWallet.Json.GetProperty("chain").GetString());
        Assert.Equal($"wallet-{user.Id}-solana", createWallet.IdempotencyKey);

        // Bridge redelivers; nothing happens twice.
        using (var again = await DeliverWebhookAsync(approved))
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Single(app.Bridge.RequestsTo(HttpMethod.Post, $"/v0/customers/{customerId}/wallets"));
    }

    [Fact]
    public async Task Webhooks_must_be_signed_untampered_and_recent()
    {
        var evt = KycEvent("kyc_unknown", "cust_unknown", "approved", "approved");

        using var unsigned = await DeliverWebhookAsync(evt, sign: false);
        Assert.Equal(HttpStatusCode.Unauthorized, unsigned.StatusCode);

        using var tampered = await DeliverWebhookAsync(evt, bodyOverride: JsonSerializer.Serialize(evt).Replace("approved", "rejected"));
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.StatusCode);

        using var replayed = await DeliverWebhookAsync(evt, sentAt: DateTimeOffset.UtcNow.AddMinutes(-20));
        Assert.Equal(HttpStatusCode.Unauthorized, replayed.StatusCode);

        using var genuine = await DeliverWebhookAsync(evt);
        Assert.Equal(HttpStatusCode.OK, genuine.StatusCode);
    }

    [Fact]
    public async Task Money_moves_between_Bridge_wallets_and_the_chat_follows_it()
    {
        var alice = await VerifiedUserAsync("Alice Money");
        var bob = await VerifiedUserAsync("Bob Money");
        var chat = await alice.User.StartDirectChatAsync(bob.User);
        var bridgeTransferId = NewId("tr");
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { id = bridgeTransferId, state = "awaiting_funds", amount = "25.5" });
        await using var bobHub = await app.ConnectAsync(bob.User);

        var message = await alice.User.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages",
            new SendMessageRequest(chat.Id, MessageKinds.BankTransfer, ContractJson.ToElement(new BankTransferPayload(bob.User.Id, "25.50", "USDC", "rent")), Guid.NewGuid()));
        var transferId = StateOf(message.State!.Value).TransferId;

        var request = app.Bridge.RequestsTo(HttpMethod.Post, "/v0/transfers").Last();
        var body = request.Json;
        Assert.Equal(transferId.ToString(), request.IdempotencyKey);
        Assert.Equal("25.5", body.GetProperty("amount").GetString());
        Assert.Equal(alice.CustomerId, body.GetProperty("on_behalf_of").GetString());
        var source = body.GetProperty("source");
        Assert.Equal(("bridge_wallet", "usdc", alice.WalletId), (source.GetProperty("payment_rail").GetString(), source.GetProperty("currency").GetString(), source.GetProperty("bridge_wallet_id").GetString()));
        var destination = body.GetProperty("destination");
        Assert.Equal(("solana", "usdc", bob.Address), (destination.GetProperty("payment_rail").GetString(), destination.GetProperty("currency").GetString(), destination.GetProperty("to_address").GetString()));

        var stored = await app.DbAsync(db => Task.FromResult(db.BankTransfers.Single(t => t.Id == transferId)));
        Assert.Equal((bridgeTransferId, TransferStatuses.Processing), (stored.BridgeTransferId, stored.Status));

        var confirmed = Wait.ForEventAsync<MessageStateDto>(bobHub, nameof(IChatClient.MessageStateChanged),
            s => s.MessageId == message.Id && StateOf(s.State).Status == TransferStatuses.Confirmed);
        using var webhook = await DeliverWebhookAsync(new
        {
            event_id = NewId("wh"),
            event_category = "transfer",
            event_type = "transfer.updated.status_transitioned",
            event_object_id = bridgeTransferId,
            event_object_status = "payment_processed",
            event_object = new { id = bridgeTransferId, state = "payment_processed" }
        });
        Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        Assert.Equal("payment_processed", StateOf((await confirmed).State).ProviderState);
    }

    [Fact]
    public async Task Money_cannot_go_to_someone_unverified()
    {
        var alice = await VerifiedUserAsync("Alice Verified");
        var bob = await app.CreateUserAsync("Bob Unverified");
        var chat = await alice.User.StartDirectChatAsync(bob);

        using var response = await alice.User.Http.PostAsJsonAsync($"api/conversations/{chat.Id}/messages",
            new SendMessageRequest(chat.Id, MessageKinds.BankTransfer, ContractJson.ToElement(new BankTransferPayload(bob.Id, "10", "usdc", null)), Guid.NewGuid()),
            ContractJson.Options);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("hasn't verified their account", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_transfer_Bridge_declines_is_marked_failed()
    {
        var alice = await VerifiedUserAsync("Alice Declined");
        var bob = await VerifiedUserAsync("Bob Declined");
        var chat = await alice.User.StartDirectChatAsync(bob.User);
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { code = "insufficient_funds", message = "Insufficient funds" }, HttpStatusCode.BadRequest);

        var message = await alice.User.PostAsync<MessageDto>($"api/conversations/{chat.Id}/messages",
            new SendMessageRequest(chat.Id, MessageKinds.BankTransfer, ContractJson.ToElement(new BankTransferPayload(bob.User.Id, "5", "usdc", null)), Guid.NewGuid()));

        var history = await bob.User.GetAsync<List<MessageDto>>($"api/conversations/{chat.Id}/messages");
        var state = StateOf(history.Single(m => m.Id == message.Id).State!.Value);
        Assert.Equal(TransferStatuses.Failed, state.Status);
        Assert.Equal("The bank declined this transfer.", state.Error);
    }
}
