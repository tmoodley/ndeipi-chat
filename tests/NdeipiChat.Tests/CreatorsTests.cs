using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NdeipiChat.Api.Creators;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;
using SkiaSharp;

namespace NdeipiChat.Tests;

/// <summary>
/// The Creator Module against NDEIPI-SRS-CREATOR-001: becoming a creator (FR-CR-01..03), tiers and
/// gated posts (FR-CR-04, 05, 08..10), subscribing, renewals and grace (FR-CR-06, 07), the fee split
/// and earnings (FR-CR-11, 12), withdrawals (FR-CR-13) and the studio (FR-CR-14..16).
/// </summary>
public sealed class CreatorsTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = CreatorsContract.BasePath;

    static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..(prefix.Length + 13)];

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    /// <summary>A verified user with a Bridge wallet holding <paramref name="balance"/> USDC.</summary>
    async Task<TestUser> VerifiedAsync(string name, decimal balance = 100m)
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
            new { id = wallet, chain = "solana", address = "So1ana", balances = new[] { new { balance = balance.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), currency = "usdc", chain = "solana" } } });
        return user;
    }

    /// <summary>Every transfer goes through at once, each with its own Bridge id.</summary>
    void TransfersSucceed() => app.Bridge.On(HttpMethod.Post, "/v0/transfers", _ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { id = NewId("tr"), state = "payment_processed" }), System.Text.Encoding.UTF8, "application/json")
    });
    void TransfersFail() => app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { code = "insufficient_funds", message = "Insufficient funds" }, HttpStatusCode.BadRequest);

    async Task<(TestUser Creator, CreatorTierDto Bronze, CreatorTierDto Gold)> CreatorAsync(string name)
    {
        var creator = await VerifiedAsync(name);
        await creator.Http.PutAsJsonAsync($"{Base}/profile", new SaveCreatorRequest(name, "music", "Afro-jazz from Lusaka.", [new CreatorLink("YouTube", "https://youtube.com/@x")]), ContractJson.Options);
        var bronze = await creator.PostAsync<CreatorTierDto>($"{Base}/studio/tiers", new SaveCreatorTierRequest("Bronze", "Early access", 5m, 50m, 1));
        var gold = await creator.PostAsync<CreatorTierDto>($"{Base}/studio/tiers", new SaveCreatorTierRequest("Gold", "Everything, plus live sessions", 20m, null, 2));
        return (creator, bronze, gold);
    }

    static async Task<CreatorMediaDto> UploadPhotoAsync(TestUser creator)
    {
        using var bitmap = new SKBitmap(1600, 900);
        using (var canvas = new SKCanvas(bitmap))
            canvas.Clear(SKColors.Purple);
        using var form = new MultipartFormDataContent();
        var part = new ByteArrayContent(SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100).ToArray());
        part.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(part, "file", "cover.png");
        using var response = await creator.Http.PostAsync($"{Base}/studio/media", form);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<CreatorMediaDto>(ContractJson.Options))!;
    }

    async Task RunRenewalsAsync()
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<CreatorBilling>().RenewDueAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Only_someone_whose_identity_is_checked_can_become_a_creator_with_up_to_five_ranked_tiers()
    {
        var unverified = await app.CreateUserAsync("Not Yet Verified");
        Assert.False((await unverified.GetAsync<CreatorsMeDto>($"{Base}/me")).CanApply);
        using (var refused = await unverified.Http.PutAsJsonAsync($"{Base}/profile", new SaveCreatorRequest("Me", "music", null, null), ContractJson.Options))
            Assert.Contains("Verify your identity", await ProblemAsync(refused));

        var (creator, bronze, gold) = await CreatorAsync("Mama Chaz");
        var me = await creator.GetAsync<CreatorsMeDto>($"{Base}/me");
        Assert.Equal((true, 5m), (me.IsCreator, me.FeePercent));

        using (var clash = await creator.Http.PostAsJsonAsync($"{Base}/studio/tiers", new SaveCreatorTierRequest("Silver", null, 10m, null, 2), ContractJson.Options))
            Assert.Contains("rank", await ProblemAsync(clash));
        for (var rank = 3; rank <= CreatorsContract.MaxTiers; rank++)
            await creator.PostAsync<CreatorTierDto>($"{Base}/studio/tiers", new SaveCreatorTierRequest($"Tier {rank}", null, rank * 10m, null, rank));
        using (var sixth = await creator.Http.PostAsJsonAsync($"{Base}/studio/tiers", new SaveCreatorTierRequest("Sixth", null, 99m, null, 5), ContractJson.Options))
            Assert.Contains("up to 5 tiers", await ProblemAsync(sixth));

        var fan = await app.CreateUserAsync("Storefront Visitor");
        var store = await fan.GetAsync<CreatorDto>($"{Base}/{creator.Id}");
        Assert.Equal(("Mama Chaz", "music", 5, false), (store.Name, store.Category, store.Tiers.Count, store.IsMe));
        Assert.Equal([bronze.Id, gold.Id], store.Tiers.Take(2).Select(t => t.Id));
        Assert.Contains(await fan.GetAsync<List<CreatorCardDto>>($"{Base}/?category=music"), c => c.UserId == creator.Id && c.FromPrice == 5m);
    }

    [Fact]
    public async Task Posts_are_locked_to_their_tier_and_up_scheduled_ones_wait_and_media_is_only_served_by_expiring_links()
    {
        var (creator, bronze, gold) = await CreatorAsync("Gated Creator");
        var photo = await UploadPhotoAsync(creator);
        Assert.NotNull(photo.Url);
        var open = await creator.PostAsync<CreatorPostDto>($"{Base}/studio/posts", new SaveCreatorPostRequest("Welcome", "Hello everyone!", null, PostAccess.Public, null, null, null));
        var goldPost = await creator.PostAsync<CreatorPostDto>($"{Base}/studio/posts",
            new SaveCreatorPostRequest("Studio session", "The full session, for Gold.", [photo.Id], PostAccess.Tier, gold.Id, null, null));
        var later = await creator.PostAsync<CreatorPostDto>($"{Base}/studio/posts",
            new SaveCreatorPostRequest("New single", "Out Friday", null, PostAccess.Public, null, null, DateTimeOffset.UtcNow.AddDays(3)));
        Assert.True(later.IsScheduled);

        var stranger = await app.CreateUserAsync("Gated Stranger");
        var page = await stranger.GetAsync<CreatorPostPageDto>($"{Base}/{creator.Id}/posts");
        Assert.DoesNotContain(page.Posts, p => p.Id == later.Id);
        var locked = page.Posts.Single(p => p.Id == goldPost.Id);
        Assert.Equal((true, (string?)null, "The full session, for Gold."), (locked.Locked, locked.Body, locked.Teaser));
        Assert.Null(Assert.Single(locked.Media).Url);
        Assert.False(page.Posts.Single(p => p.Id == open.Id).Locked);
        using (var hidden = await stranger.Http.GetAsync($"{Base}/posts/{later.Id}"))
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        // A Bronze subscriber still can't see Gold; a Gold one can, media included.
        TransfersSucceed();
        var bronzeFan = await VerifiedAsync("Bronze Fan");
        await bronzeFan.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(bronze.Id, BillingPeriods.Monthly));
        Assert.True((await bronzeFan.GetAsync<CreatorPostDto>($"{Base}/posts/{goldPost.Id}")).Locked);
        var goldFan = await VerifiedAsync("Gold Fan");
        await goldFan.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(gold.Id, BillingPeriods.Monthly));
        var seen = await goldFan.GetAsync<CreatorPostDto>($"{Base}/posts/{goldPost.Id}");
        Assert.Equal((false, "The full session, for Gold."), (seen.Locked, seen.Body));

        // The link works without sign-in while it lasts; a made-up one doesn't.
        using (var file = await app.CreateClient().GetAsync(seen.Media[0].Url))
            Assert.Equal("image/jpeg", file.Content.Headers.ContentType?.MediaType);
        using (var forged = await app.CreateClient().GetAsync($"{Base}/media/not-a-token"))
            Assert.Equal(HttpStatusCode.NotFound, forged.StatusCode);
    }

    [Fact]
    public async Task Subscribing_pays_the_creator_from_the_wallet_with_the_platform_fee_split_off_and_a_failed_payment_gives_no_access()
    {
        var (creator, bronze, _) = await CreatorAsync("Split Creator");
        var post = await creator.PostAsync<CreatorPostDto>($"{Base}/studio/posts", new SaveCreatorPostRequest("Bronze only", "Thanks!", null, PostAccess.Tier, bronze.Id, null, null));

        var unverified = await app.CreateUserAsync("No Wallet Fan");
        using (var noWallet = await unverified.Http.PostAsJsonAsync($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(bronze.Id, BillingPeriods.Monthly), ContractJson.Options))
            Assert.Contains("Verify your identity", await ProblemAsync(noWallet));

        TransfersSucceed();
        var fan = await VerifiedAsync("Paying Fan");
        var sub = await fan.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(bronze.Id, BillingPeriods.Annual, ShareProfile: true));
        Assert.Equal((SubscriptionStatuses.Active, 50m, TransferStatuses.Confirmed), (sub.Status, sub.Price, sub.LastPaymentStatus));
        Assert.InRange(sub.CurrentPeriodEnd, DateTimeOffset.UtcNow.AddDays(364), DateTimeOffset.UtcNow.AddDays(367));

        // One transfer: $50 from the fan's wallet, Bridge keeping 5% ($2.50) for the platform.
        var transfer = app.Bridge.RequestsTo(HttpMethod.Post, "/v0/transfers").Last().Json;
        Assert.Equal(("50", "2.5"), (transfer.GetProperty("amount").GetString(), transfer.GetProperty("developer_fee").GetString()));
        var payment = await app.DbAsync(db => db.CreatorPayments.SingleAsync(p => p.SubscriptionId == sub.Id));
        Assert.Equal((50m, 2.5m, 5m, CreatorPaymentKinds.Subscription), (payment.Amount, payment.Fee, payment.FeePercent, payment.Kind));
        Assert.False((await fan.GetAsync<CreatorPostDto>($"{Base}/posts/{post.Id}")).Locked);

        // Not enough in the wallet: no subscription, no access.
        TransfersFail();
        var broke = await VerifiedAsync("Broke Fan", 0m);
        var failed = await broke.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(bronze.Id, BillingPeriods.Monthly));
        Assert.Equal((SubscriptionStatuses.Ended, TransferStatuses.Failed), (failed.Status, failed.LastPaymentStatus));
        Assert.True((await broke.GetAsync<CreatorPostDto>($"{Base}/posts/{post.Id}")).Locked);

        // Pay-per-view: pay once, see it from then on.
        TransfersSucceed();
        var ppv = await creator.PostAsync<CreatorPostDto>($"{Base}/studio/posts", new SaveCreatorPostRequest("Live album", "Track list…", null, PostAccess.PayPerView, null, 3m, null));
        var buyer = await VerifiedAsync("Album Buyer");
        Assert.True((await buyer.GetAsync<CreatorPostDto>($"{Base}/posts/{ppv.Id}")).Locked);
        var bought = await buyer.PostAsync<CreatorPostDto>($"{Base}/posts/{ppv.Id}/unlock", new { });
        Assert.Equal((false, true), (bought.Locked, bought.Unlocked));
    }

    [Fact]
    public async Task Renewals_charge_each_period_a_failed_one_gives_three_days_grace_and_cancelling_ends_at_the_period_end()
    {
        var (creator, bronze, _) = await CreatorAsync("Renewing Creator");
        var post = await creator.PostAsync<CreatorPostDto>($"{Base}/studio/posts", new SaveCreatorPostRequest("Members", "Hi", null, PostAccess.Tier, bronze.Id, null, null));
        TransfersSucceed();
        var fan = await VerifiedAsync("Renewing Fan");
        var sub = await fan.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(bronze.Id, BillingPeriods.Monthly));

        // The month is up and the renewal fails: grace, with access, for three days.
        await app.DbAsync(db => db.CreatorSubscriptions.Where(s => s.Id == sub.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentPeriodEnd, DateTimeOffset.UtcNow.AddMinutes(-1)).SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        TransfersFail();
        await RunRenewalsAsync();
        var me = await fan.GetAsync<CreatorsMeDto>($"{Base}/me");
        var grace = me.Subscriptions.Single(s => s.Id == sub.Id);
        Assert.Equal(SubscriptionStatuses.Grace, grace.Status);
        Assert.InRange(grace.GraceUntil!.Value, DateTimeOffset.UtcNow.AddDays(2.9), DateTimeOffset.UtcNow.AddDays(3.1));
        Assert.False((await fan.GetAsync<CreatorPostDto>($"{Base}/posts/{post.Id}")).Locked);

        // Retried the next day, it goes through: a new month, active again.
        await app.DbAsync(db => db.CreatorSubscriptions.Where(s => s.Id == sub.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        TransfersSucceed();
        await RunRenewalsAsync();
        var renewed = (await fan.GetAsync<CreatorsMeDto>($"{Base}/me")).Subscriptions.Single(s => s.Id == sub.Id);
        Assert.Equal((SubscriptionStatuses.Active, (DateTimeOffset?)null), (renewed.Status, renewed.GraceUntil));
        Assert.True(renewed.CurrentPeriodEnd > DateTimeOffset.UtcNow.AddDays(25));
        Assert.Equal(2, await app.DbAsync(db => db.CreatorPayments.CountAsync(p => p.SubscriptionId == sub.Id && p.Status == TransferStatuses.Confirmed)));

        // Cancelled: no more charges (still the first payment and the two renewal tries); it ends when the paid month does.
        await fan.PostAsync<CreatorSubscriptionDto>($"{Base}/subscriptions/{sub.Id}/cancel", new { });
        await app.DbAsync(db => db.CreatorSubscriptions.Where(s => s.Id == sub.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await RunRenewalsAsync();
        Assert.Equal(SubscriptionStatuses.Ended, (await fan.GetAsync<CreatorsMeDto>($"{Base}/me")).Subscriptions.Single(s => s.Id == sub.Id).Status);
        Assert.Equal(3, await app.DbAsync(db => db.CreatorPayments.CountAsync(p => p.SubscriptionId == sub.Id)));
        Assert.True((await fan.GetAsync<CreatorPostDto>($"{Base}/posts/{post.Id}")).Locked);
    }

    [Fact]
    public async Task Grace_runs_out_without_a_payment_and_access_ends()
    {
        var (creator, bronze, _) = await CreatorAsync("Lapsing Creator");
        TransfersSucceed();
        var fan = await VerifiedAsync("Lapsing Fan");
        var sub = await fan.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(bronze.Id, BillingPeriods.Monthly));
        await app.DbAsync(db => db.CreatorSubscriptions.Where(s => s.Id == sub.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Status, SubscriptionStatuses.Grace)
            .SetProperty(x => x.GraceUntil, DateTimeOffset.UtcNow.AddMinutes(-1))
            .SetProperty(x => x.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await RunRenewalsAsync();
        Assert.Equal(SubscriptionStatuses.Ended, (await fan.GetAsync<CreatorsMeDto>($"{Base}/me")).Subscriptions.Single(s => s.Id == sub.Id).Status);
    }

    [Fact]
    public async Task The_studio_shows_earnings_subscribers_and_analytics_and_broadcasts_to_subscribers_inboxes()
    {
        var (creator, bronze, gold) = await CreatorAsync("Studio Creator");
        var post = await creator.PostAsync<CreatorPostDto>($"{Base}/studio/posts", new SaveCreatorPostRequest("Hello", "Public post", null, PostAccess.Public, null, null, null));
        TransfersSucceed();
        var named = await VerifiedAsync("Named Fan");
        var anonymous = await VerifiedAsync("Shy Fan");
        await named.GetAsync<CreatorDto>($"{Base}/{creator.Id}");
        await anonymous.GetAsync<CreatorDto>($"{Base}/{creator.Id}");
        await named.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(gold.Id, BillingPeriods.Monthly, ShareProfile: true));
        await anonymous.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(bronze.Id, BillingPeriods.Monthly));
        await named.GetAsync<CreatorPostDto>($"{Base}/posts/{post.Id}");
        await named.GetAsync<CreatorPostDto>($"{Base}/posts/{post.Id}");

        var earnings = await creator.GetAsync<CreatorEarningsDto>($"{Base}/studio/earnings");
        Assert.Equal((25m, 1.25m, 23.75m, 2), (earnings.GrossThisMonth, earnings.FeesThisMonth, earnings.NetThisMonth, earnings.ActiveSubscribers));
        Assert.Equal(23.75m, earnings.Mrr);
        Assert.Equal(23.75m, earnings.OnHold);

        var subscribers = await creator.GetAsync<List<SubscriberDto>>($"{Base}/studio/subscribers");
        Assert.Contains(subscribers, s => s.Name == "Named Fan" && !s.Anonymous && s.TierName == "Gold");
        Assert.Contains(subscribers, s => s.Anonymous && s.Name.StartsWith("Subscriber #") && s.TierName == "Bronze");

        var analytics = await creator.GetAsync<CreatorAnalyticsDto>($"{Base}/studio/analytics");
        Assert.Equal((2, 2, 100d, 1), (analytics.StorefrontVisitors, analytics.NewSubscribers, analytics.ConversionPercent, analytics.PostViews));

        // Gold and up only: the named fan gets it in their chats, from the creator.
        var sent = await creator.PostAsync<BroadcastResultDto>($"{Base}/studio/broadcasts", new CreatorBroadcastRequest("New album Friday!", gold.Id));
        Assert.Equal(1, sent.Sent);
        var chats = await named.GetAsync<List<ConversationDto>>("api/conversations");
        var chat = chats.Single(c => c.Members.Any(m => m.Id == creator.Id));
        Assert.Contains("New album Friday!", ContractJson.Read<TextPayload>(chat.LastMessage!.Payload)!.Text);
        Assert.DoesNotContain(await anonymous.GetAsync<List<ConversationDto>>("api/conversations"), c => c.Members.Any(m => m.Id == creator.Id));
    }

    [Fact]
    public async Task Creators_withdraw_to_a_bank_or_USDC_address_once_earnings_are_off_hold()
    {
        var (creator, bronze, _) = await CreatorAsync("Withdrawing Creator");
        var customer = await app.DbAsync(db => db.BankingProfiles.Where(p => p.UserId == creator.Id).Select(p => p.BridgeCustomerId).SingleAsync());
        app.Bridge.OnJson(HttpMethod.Post, $"/v0/customers/{customer}/external_accounts", new { id = "ea_123", last_4 = "6789", bank_name = "Chase", currency = "usd" });

        using (var bad = await creator.Http.PostAsJsonAsync($"{Base}/studio/payout-accounts", new SavePayoutAccountRequest(PayoutRails.Ach, "Chaz M", "Chase", "123", "12"), ContractJson.Options))
            Assert.Contains("routing", await ProblemAsync(bad));
        var bank = await creator.PostAsync<PayoutAccountDto>($"{Base}/studio/payout-accounts",
            new SavePayoutAccountRequest(PayoutRails.Ach, "Chaz M", "Chase", "000123456789", "021000021", "checking", StreetLine1: "1 Main St", City: "Austin", State: "TX", PostalCode: "78701"));
        Assert.Equal("Chase ••6789", bank.Label);
        var sent = app.Bridge.RequestsTo(HttpMethod.Post, $"/v0/customers/{customer}/external_accounts").Last().Json;
        Assert.Equal("021000021", sent.GetProperty("account").GetProperty("routing_number").GetString());
        Assert.False(await app.DbAsync(db => db.PayoutAccounts.AnyAsync(a => a.UserId == creator.Id && a.Label.Contains("123456"))));

        using (var badAddress = await creator.Http.PostAsJsonAsync($"{Base}/studio/payout-accounts", new SavePayoutAccountRequest(PayoutRails.Crypto, "Exchange", Address: "0xnotsolana"), ContractJson.Options))
            Assert.Contains("solana address", await ProblemAsync(badAddress));

        // $95 earned today (the wallet stub holds $100): on hold, so only $5 can go.
        TransfersSucceed();
        var fan = await VerifiedAsync("Big Fan");
        var big = await creator.PostAsync<CreatorTierDto>($"{Base}/studio/tiers", new SaveCreatorTierRequest("Patron", null, 100m, null, 3));
        await fan.PostAsync<CreatorSubscriptionDto>($"{Base}/{creator.Id}/subscribe", new CreatorSubscribeRequest(big.Id, BillingPeriods.Monthly));
        var payouts = await creator.GetAsync<PayoutsDto>($"{Base}/studio/payouts");
        Assert.Equal((95m, 5m), (payouts.OnHold, payouts.Withdrawable));
        using (var tooMuch = await creator.Http.PostAsJsonAsync($"{Base}/studio/withdrawals", new WithdrawRequest(bank.Id, 50m), ContractJson.Options))
            Assert.Contains("on hold", await ProblemAsync(tooMuch));

        // A week later it's free to withdraw: a Bridge transfer to the saved bank account.
        await app.DbAsync(db => db.CreatorPayments.Where(p => p.CreatorId == creator.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.ConfirmedAt, DateTimeOffset.UtcNow.AddDays(-8))));
        app.Bridge.OnJson(HttpMethod.Post, "/v0/transfers", new { id = "tr_withdraw", state = "awaiting_funds" });
        var withdrawal = await creator.PostAsync<WithdrawalDto>($"{Base}/studio/withdrawals", new WithdrawRequest(bank.Id, 50m));
        Assert.Equal((PayoutStatuses.Processing, "Chase ••6789"), (withdrawal.Status, withdrawal.AccountLabel));
        var request = app.Bridge.RequestsTo(HttpMethod.Post, "/v0/transfers").Last();
        Assert.Equal(($"withdrawal-{withdrawal.Id}", "ach", "ea_123"),
            (request.IdempotencyKey, request.Json.GetProperty("destination").GetProperty("payment_rail").GetString(),
             request.Json.GetProperty("destination").GetProperty("external_account_id").GetString()));
        // Still on its way, it counts against what's left.
        Assert.Equal(50m, (await creator.GetAsync<PayoutsDto>($"{Base}/studio/payouts")).Withdrawable);
    }
}
