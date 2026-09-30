using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>The Donations sub-app against its SRS: campaigns (FR-02), one-tap gifts (FR-01), Believe Points (FR-03) and receipts (FR-04).</summary>
public sealed class DonationsTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = DonationsContract.BasePath;

    static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    Task<TestUser> OrganizerAsync(string name) => app.CreateUserAsync(name, $"{Guid.NewGuid():N}@example.test", roles: [DonationsContract.OrganizerRole]);

    Task VerifyBankingAsync(TestUser user) => app.DbAsync(db =>
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

    static SaveCampaignRequest Shoes(decimal target = 500) =>
        new("School shoes for Mbare kids", "Every child at the home starts term in proper shoes.", "Forty children, forty pairs.", "Mbare Children's Home", "👟", target, null);

    async Task<CampaignDto> LiveAsync(TestUser organizer, SaveCampaignRequest? request = null)
    {
        var draft = await organizer.PostAsync<CampaignDto>($"{Base}/campaigns", request ?? Shoes());
        return await organizer.PostAsync<CampaignDto>($"{Base}/campaigns/{draft.Id}/publish", new { });
    }

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

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
        var signature = Convert.ToBase64String(TestApp.WebhookKey.SignData(Encoding.UTF8.GetBytes($"{timestamp}.{body}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var request = new HttpRequestMessage(HttpMethod.Post, "webhooks/bridge") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Webhook-Signature", $"t={timestamp},v0={signature}");
        return await app.CreateClient().SendAsync(request);
    }

    [Fact]
    public async Task Only_organizers_run_campaigns_and_drafts_stay_private_until_published()
    {
        var member = await app.CreateUserAsync("Plain Member");
        using (var refused = await member.Http.PostAsJsonAsync($"{Base}/campaigns", Shoes(), ContractJson.Options))
            Assert.Contains("Only organizers", await ProblemAsync(refused));
        Assert.False((await member.GetAsync<DonationsMeDto>($"{Base}/me")).IsOrganizer);

        var organizer = await OrganizerAsync("Chipo Organizer");
        var draft = await organizer.PostAsync<CampaignDto>($"{Base}/campaigns", Shoes());
        Assert.Equal((CampaignStatuses.Draft, "usdc", false), (draft.Status, draft.Currency, draft.Verified));

        // A draft isn't in the feed and others can't open it.
        Assert.DoesNotContain(await member.GetAsync<List<CampaignDto>>($"{Base}/campaigns"), c => c.Id == draft.Id);
        using (var hidden = await member.Http.GetAsync($"{Base}/campaigns/{draft.Id}"))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        await organizer.PostAsync<CampaignDto>($"{Base}/campaigns/{draft.Id}/publish", new { });
        Assert.Contains(await member.GetAsync<List<CampaignDto>>($"{Base}/campaigns"), c => c.Id == draft.Id);

        // Bad input is refused, in words.
        using (var noTarget = await organizer.Http.PostAsJsonAsync($"{Base}/campaigns", Shoes(0), ContractJson.Options))
            Assert.Contains("target", await ProblemAsync(noTarget));
    }

    [Fact]
    public async Task Admins_verify_campaigns_and_verified_ones_lead_the_feed_until_their_details_change()
    {
        var organizer = await OrganizerAsync("Verify Organizer");
        var admin = await app.CreateUserAsync("Ndeipi Admin", roles: [DonationsContract.VerifierRole]);
        var first = await LiveAsync(organizer, Shoes() with { Title = "First campaign" });
        var second = await LiveAsync(organizer, Shoes() with { Title = "Second campaign" });

        using (var notAdmin = await organizer.Http.PutAsync($"{Base}/campaigns/{first.Id}/verified?verified=true", null))
            Assert.Contains("Only Ndeipi admins", await ProblemAsync(notAdmin));
        using (var verify = await admin.Http.PutAsync($"{Base}/campaigns/{first.Id}/verified?verified=true", null))
            Assert.True((await verify.Content.ReadFromJsonAsync<CampaignDto>(ContractJson.Options))!.Verified);

        // The verified one comes first, though it's older.
        var feed = await admin.GetAsync<List<CampaignDto>>($"{Base}/campaigns");
        Assert.True(feed.FindIndex(c => c.Id == first.Id) < feed.FindIndex(c => c.Id == second.Id));

        // Renaming it takes the badge away until it's checked again.
        using var renamed = await organizer.Http.PutAsJsonAsync($"{Base}/campaigns/{first.Id}", Shoes() with { Title = "Something else entirely" }, ContractJson.Options);
        Assert.False((await renamed.Content.ReadFromJsonAsync<CampaignDto>(ContractJson.Options))!.Verified);
    }

    [Fact]
    public async Task A_gift_counts_earns_points_once_and_gets_a_checkable_receipt_when_Bridge_confirms_it()
    {
        var organizer = await OrganizerAsync("Gift Organizer");
        var donor = await app.CreateUserAsync("Tendai Giver");
        var campaign = await LiveAsync(organizer);

        // Both wallets have to be verified.
        using (var unverified = await donor.Http.PostAsJsonAsync($"{Base}/campaigns/{campaign.Id}/donations", new DonateRequest(5, false, null), ContractJson.Options))
            Assert.Contains("Verify your identity", await ProblemAsync(unverified));
        await VerifyBankingAsync(donor);
        using (var organizerNotReady = await donor.Http.PostAsJsonAsync($"{Base}/campaigns/{campaign.Id}/donations", new DonateRequest(5, false, null), ContractJson.Options))
            Assert.Contains("organizer's wallet", await ProblemAsync(organizerNotReady));
        await VerifyBankingAsync(organizer);

        // Organizers can't give to their own.
        using (var own = await organizer.Http.PostAsJsonAsync($"{Base}/campaigns/{campaign.Id}/donations", new DonateRequest(5, false, null), ContractJson.Options))
            Assert.Contains("your own campaign", await ProblemAsync(own));

        var bridgeTransferId = NewId("tr");
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { id = bridgeTransferId, state = "awaiting_funds" });
        var gift = await donor.PostAsync<DonationDto>($"{Base}/campaigns/{campaign.Id}/donations", new DonateRequest(25, false, "Go well!"));
        Assert.Equal((DonationStatuses.Pending, 0, (string?)null), (gift.Status, gift.Points, gift.ReceiptHash));

        // The whole gift, no fee, from the giver's wallet to the organizer's.
        var transfer = await app.DbAsync(db => db.BankTransfers.AsNoTracking().SingleAsync(t => t.BridgeTransferId == bridgeTransferId));
        Assert.Equal((donor.Id, organizer.Id, 25m, "usdc"), (transfer.SenderId, transfer.RecipientId, transfer.Amount, transfer.Currency));

        // Not counted until it's confirmed.
        Assert.Equal(0m, (await donor.GetAsync<CampaignDetailDto>($"{Base}/campaigns/{campaign.Id}")).Campaign.Raised);

        using (var webhook = await DeliverTransferWebhookAsync(bridgeTransferId, "payment_processed"))
            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        using (var again = await DeliverTransferWebhookAsync(bridgeTransferId, "payment_processed"))
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var detail = await donor.GetAsync<CampaignDetailDto>($"{Base}/campaigns/{campaign.Id}");
        Assert.Equal((25m, 1), (detail.Campaign.Raised, detail.Campaign.Gifts));
        var shown = Assert.Single(detail.Recent);
        Assert.Equal(("Tendai Giver", "Go well!"), (shown.DonorName, shown.Message));

        // 25 × 10 points, once, in the balance every Ndeipi app shares.
        var confirmed = await donor.GetAsync<DonationDto>($"{Base}/donations/{gift.Id}");
        Assert.Equal((DonationStatuses.Confirmed, 250, bridgeTransferId), (confirmed.Status, confirmed.Points, confirmed.TransferReference));
        var points = await donor.GetAsync<PointsDto>(PointsContract.BasePath);
        Assert.Equal(250, points.Balance);
        Assert.Equal(DonationsContract.AppId, Assert.Single(points.Recent).Source);

        // The receipt's hash is its recorded contents, and the server vouches for it.
        Assert.Equal(DonationReceipt.Hash(DonationReceipt.Canonical(gift.Id, campaign.Id, 25, "usdc", bridgeTransferId, confirmed.ConfirmedAt!.Value, 250)), confirmed.ReceiptHash);
        var check = await organizer.GetAsync<ReceiptCheckDto>($"{Base}/receipts/{confirmed.ReceiptHash}");
        Assert.Equal((true, 25m, 250), (check.Valid, check.Amount, check.Points));
        Assert.False((await organizer.GetAsync<ReceiptCheckDto>($"{Base}/receipts/{new string('0', 64)}")).Valid);
    }

    [Fact]
    public async Task Anonymous_gifts_hide_the_giver_and_failed_ones_earn_nothing()
    {
        var organizer = await OrganizerAsync("Quiet Organizer");
        var donor = await app.CreateUserAsync("Shy Giver");
        await VerifyBankingAsync(organizer);
        await VerifyBankingAsync(donor);
        var campaign = await LiveAsync(organizer);

        var anonymousTransfer = NewId("tr");
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { id = anonymousTransfer, state = "awaiting_funds" });
        await donor.PostAsync<DonationDto>($"{Base}/campaigns/{campaign.Id}/donations", new DonateRequest(5, true, null));
        using (var webhook = await DeliverTransferWebhookAsync(anonymousTransfer, "payment_processed"))
            Assert.Equal(HttpStatusCode.OK, webhook.StatusCode);
        var seen = await organizer.GetAsync<CampaignDetailDto>($"{Base}/campaigns/{campaign.Id}");
        Assert.Null(Assert.Single(seen.Recent).DonorName);

        // Bridge turns one down: it doesn't count, and earns no points.
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { code = "insufficient_funds", message = "Insufficient funds" }, HttpStatusCode.BadRequest);
        var failed = await donor.PostAsync<DonationDto>($"{Base}/campaigns/{campaign.Id}/donations", new DonateRequest(10, false, null));
        Assert.Equal(DonationStatuses.Failed, failed.Status);
        Assert.Equal((5m, 1), ((await donor.GetAsync<CampaignDetailDto>($"{Base}/campaigns/{campaign.Id}")).Campaign.Raised,
            (await donor.GetAsync<CampaignDetailDto>($"{Base}/campaigns/{campaign.Id}")).Campaign.Gifts));
        Assert.Equal(50, (await donor.GetAsync<PointsDto>(PointsContract.BasePath)).Balance);

        // A closed campaign takes no more.
        await organizer.PostAsync<CampaignDto>($"{Base}/campaigns/{campaign.Id}/close", new { });
        using var closed = await donor.Http.PostAsJsonAsync($"{Base}/campaigns/{campaign.Id}/donations", new DonateRequest(5, false, null), ContractJson.Options);
        Assert.Contains("isn't taking gifts", await ProblemAsync(closed));
    }
}
