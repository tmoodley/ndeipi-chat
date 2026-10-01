using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Auth;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Trust;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>The Trust Score against its SRS: tiers (§3.2), history and decay (§3.3), anti-sybil and live updates (§3.4).</summary>
public sealed class TrustTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = TrustContract.BasePath;

    static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    static TrustLink Link(string platform, int daysAgo, bool premium = false) => new()
    {
        Platform = platform,
        ExternalId = Guid.NewGuid().ToString("N"),
        Premium = premium,
        LinkedAt = Now.AddDays(-daysAgo),
        LastConfirmedAt = Now.AddDays(-daysAgo)
    };

    static string NewCode() => Guid.NewGuid().ToString("N");

    HttpClient Browser() => app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    Task ApproveKycAsync(TestUser user) => app.DbAsync(db =>
    {
        db.BankingProfiles.Add(new BankingProfile
        {
            UserId = user.Id,
            KycStatus = BankingProfile.Approved,
            TosStatus = BankingProfile.Approved,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        return db.SaveChangesAsync();
    });

    /// <summary>Starts a link as the user, then comes back from the platform with the code: where it lands.</summary>
    async Task<string> LinkAsync(TestUser user, string platform, string code)
    {
        var start = await user.PostAsync<TrustLinkStartDto>($"{Base}/links/{platform}", new { });
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["state"];
        using var back = await Browser().GetAsync($"{Base}/oauth/{platform}/callback?code={code}&state={state}");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        return back.Headers.Location!.ToString();
    }

    static TelegramLoginRequest Telegram(long id, string username, DateTimeOffset signedAt)
    {
        var login = new TelegramLoginRequest(id, "Tendai", null, username, null, signedAt.ToUnixTimeSeconds(), "");
        return login with { Hash = TelegramLogin.Sign(login, TestApp.TelegramBotToken) };
    }

    [Fact]
    public void Tiers_take_their_best_account_history_adds_a_point_a_counterparty_and_the_total_caps_at_100()
    {
        Assert.Equal(0, TrustMath.Compute(false, [], 0, Now).Score);

        var identity = TrustMath.Compute(true, [], 0, Now);
        Assert.Equal((50, 50m), (identity.Score, identity.Tier1));

        // Two social accounts don't add up: tier 3 gives its weight once.
        var social = TrustMath.Compute(false, [Link(TrustPlatforms.Telegram, 1), Link(TrustPlatforms.Facebook, 1)], 0, Now);
        Assert.Equal((20, 20m), (social.Score, social.Tier3));

        // X counts as professional only with Premium.
        Assert.Equal(30m, TrustMath.Compute(false, [Link(TrustPlatforms.X, 1, premium: true)], 0, Now).Tier2);
        Assert.Equal((0m, 20m), (TrustMath.Compute(false, [Link(TrustPlatforms.X, 1)], 0, Now) is var x ? (x.Tier2, x.Tier3) : default));

        var history = TrustMath.Compute(false, [], 40, Now);
        Assert.Equal((15, 15m, 40), (history.Score, history.HistoryPoints, history.Settlements));

        var everything = TrustMath.Compute(true, [Link(TrustPlatforms.LinkedIn, 1), Link(TrustPlatforms.Telegram, 1)], 7, Now);
        Assert.Equal(100, everything.Score);
    }

    [Fact]
    public void Social_accounts_count_fully_for_90_days_half_until_180_then_not_at_all_and_identity_never_decays()
    {
        Assert.Equal(30m, TrustMath.Compute(false, [Link(TrustPlatforms.LinkedIn, 90)], 0, Now).Tier2);
        Assert.Equal(15m, TrustMath.Compute(false, [Link(TrustPlatforms.LinkedIn, 91)], 0, Now).Tier2);
        Assert.Equal(15m, TrustMath.Compute(false, [Link(TrustPlatforms.LinkedIn, 180)], 0, Now).Tier2);
        var stale = TrustMath.Compute(false, [Link(TrustPlatforms.LinkedIn, 181)], 0, Now);
        Assert.Equal((0m, 0), (stale.Tier2, stale.Counting.Count));

        // A fresher account in the same tier wins.
        Assert.Equal(20m, TrustMath.Compute(false, [Link(TrustPlatforms.Facebook, 150), Link(TrustPlatforms.Telegram, 3)], 0, Now).Tier3);
        Assert.Equal(50m, TrustMath.Compute(false, [Link(TrustPlatforms.Mpost, 1000)], 0, Now).Tier1);
    }

    [Fact]
    public async Task Everyone_sees_anyones_score_and_a_passed_identity_check_is_tier_1()
    {
        var owner = await app.CreateUserAsync("Rudo Trusted");
        var stranger = await app.CreateUserAsync("Curious Stranger");

        var fresh = await stranger.GetAsync<TrustScoreDto>($"{Base}/{owner.Id}");
        Assert.Equal((0, "New", false), (fresh.Score, fresh.Level, fresh.IdentityVerified));

        await ApproveKycAsync(owner);
        // The owner checks again (Bridge would be polled; here KYC is already approved).
        var mine = await owner.PostAsync<MyTrustDto>($"{Base}/refresh", new { });
        Assert.Equal(50, mine.Score.Score);
        Assert.True(mine.Links.Single(l => l.Platform == TrustPlatforms.Bridge).Linked);

        var seen = await stranger.GetAsync<TrustScoreDto>($"{Base}/{owner.Id}");
        Assert.Equal((50, "Building trust", true), (seen.Score, seen.Level, seen.IdentityVerified));
        Assert.Equal([TrustPlatforms.Bridge], seen.Tiers.Single(t => t.Tier == 1).Platforms);

        using var missing = await stranger.Http.GetAsync($"{Base}/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Linking_X_Premium_through_OAuth_counts_in_tier_2_and_its_tokens_are_kept_encrypted()
    {
        var user = await app.CreateUserAsync("Farai Premium");
        var code = NewCode();
        app.Platforms.Accounts[code] = new StubPlatforms.Account("x-" + code, "farai", "blue");

        var start = await user.PostAsync<TrustLinkStartDto>($"{Base}/links/{TrustPlatforms.X}", new { });
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query);
        Assert.StartsWith("https://x.com/i/oauth2/authorize?", start.AuthorizeUrl);
        Assert.Equal(("x-client", "S256"), (query["client_id"], query["code_challenge_method"]));
        Assert.EndsWith($"/{Base}/oauth/x/callback", query["redirect_uri"]);

        Assert.EndsWith("trust?linked=x", await LinkAsync(user, TrustPlatforms.X, code));

        var me = await user.GetAsync<MyTrustDto>($"{Base}/me");
        var x = me.Links.Single(l => l.Platform == TrustPlatforms.X);
        Assert.Equal((true, "@farai", 2, 1m), (x.Linked, x.Handle, x.Tier, x.Credit));
        Assert.Equal((30, 30m), (me.Score.Score, me.Score.Tiers.Single(t => t.Tier == 2).Earned));

        var stored = await app.DbAsync(db => db.TrustLinks.SingleAsync(l => l.UserId == user.Id));
        Assert.NotNull(stored.AccessTokenProtected);
        Assert.DoesNotContain("at-", stored.AccessTokenProtected);
    }

    [Fact]
    public async Task An_outside_account_backs_only_one_Ndeipi_account()
    {
        var first = await app.CreateUserAsync("Original Owner");
        var second = await app.CreateUserAsync("Second Account");
        var account = new StubPlatforms.Account("li-" + NewCode(), "Nyasha M");
        var (one, two) = (NewCode(), NewCode());
        app.Platforms.Accounts[one] = account;
        app.Platforms.Accounts[two] = account;

        Assert.EndsWith("linked=linkedin", await LinkAsync(first, TrustPlatforms.LinkedIn, one));
        var refused = Uri.UnescapeDataString(await LinkAsync(second, TrustPlatforms.LinkedIn, two));
        Assert.Contains("already linked to another Ndeipi account", refused);
        Assert.Equal(0, (await second.GetAsync<MyTrustDto>($"{Base}/me")).Score.Score);

        // Once the first lets it go, it can back the second.
        await first.Http.DeleteAsync($"{Base}/links/{TrustPlatforms.LinkedIn}");
        var three = NewCode();
        app.Platforms.Accounts[three] = account;
        Assert.EndsWith("linked=linkedin", await LinkAsync(second, TrustPlatforms.LinkedIn, three));
        Assert.Equal(30, (await second.GetAsync<MyTrustDto>($"{Base}/me")).Score.Score);
    }

    [Fact]
    public async Task A_callback_without_a_live_state_links_nothing()
    {
        var code = NewCode();
        app.Platforms.Accounts[code] = new StubPlatforms.Account("x-" + code, "nobody");
        using var back = await Browser().GetAsync($"{Base}/oauth/x/callback?code={code}&state=made-up");
        Assert.Contains("error=", back.Headers.Location!.ToString());

        var user = await app.CreateUserAsync("Declined Linker");
        var start = await user.PostAsync<TrustLinkStartDto>($"{Base}/links/{TrustPlatforms.LinkedIn}", new { });
        var state = System.Web.HttpUtility.ParseQueryString(new Uri(start.AuthorizeUrl).Query)["state"];
        using var declined = await Browser().GetAsync($"{Base}/oauth/linkedin/callback?error=user_cancelled_authorize&state={state}");
        Assert.Contains("wasn't linked", Uri.UnescapeDataString(declined.Headers.Location!.ToString()));

        // Facebook has no app configured on this server.
        using var unavailable = await user.Http.PostAsJsonAsync($"{Base}/links/{TrustPlatforms.Facebook}", new { });
        Assert.Equal(HttpStatusCode.BadRequest, unavailable.StatusCode);
        Assert.False((await user.GetAsync<MyTrustDto>($"{Base}/me")).Links.Single(l => l.Platform == TrustPlatforms.Facebook).Available);
    }

    [Fact]
    public async Task Telegram_links_with_the_widgets_signed_login_and_the_score_is_broadcast_live()
    {
        var user = await app.CreateUserAsync("Tendai Telegram");
        var watcher = await app.CreateUserAsync("Score Watcher");
        Assert.Equal("ndeipi_test_bot", (await user.GetAsync<TrustSettingsDto>($"{Base}/settings")).TelegramBot);

        var id = Random.Shared.NextInt64(1, long.MaxValue);
        var tampered = Telegram(id, "tendai", DateTimeOffset.UtcNow) with { Username = "someone_else" };
        using (var refused = await user.Http.PostAsJsonAsync($"{Base}/links/telegram", tampered, ContractJson.Options))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        using (var old = await user.Http.PostAsJsonAsync($"{Base}/links/telegram", Telegram(id, "tendai", DateTimeOffset.UtcNow.AddDays(-2)), ContractJson.Options))
            Assert.Equal(HttpStatusCode.BadRequest, old.StatusCode);

        await using var hub = await app.ConnectAsync(watcher);
        await hub.InvokeAsync(ChatHubContract.Subscribe, TrustContract.Topic(user.Id));
        var live = Wait.ForEventAsync<TopicMessageDto>(hub, nameof(IChatClient.TopicMessage),
            m => m.Topic == TrustContract.Topic(user.Id) && ContractJson.Read<TrustScoreDto>(m.Payload)!.Score == 20);

        var me = await user.PostAsync<MyTrustDto>($"{Base}/links/telegram", Telegram(id, "tendai", DateTimeOffset.UtcNow));
        Assert.Equal((20, "@tendai"), (me.Score.Score, me.Links.Single(l => l.Platform == TrustPlatforms.Telegram).Handle));
        Assert.Equal([TrustPlatforms.Telegram], ContractJson.Read<TrustScoreDto>((await live).Payload)!.Tiers.Single(t => t.Tier == 3).Platforms);
    }

    [Fact]
    public async Task Settlements_with_different_people_in_the_past_year_earn_history_points()
    {
        var user = await app.CreateUserAsync("Busy Trader");
        var a = await app.CreateUserAsync("Counterparty A");
        var b = await app.CreateUserAsync("Counterparty B");
        var c = await app.CreateUserAsync("Counterparty C");
        var now = DateTimeOffset.UtcNow;
        BankTransfer Transfer(Guid from, Guid to, string status, int daysAgo) => new()
        {
            Id = Guid.NewGuid(), SenderId = from, RecipientId = to, Amount = 5, Currency = "usdc", Status = status,
            CreatedAt = now.AddDays(-daysAgo), UpdatedAt = now.AddDays(-daysAgo)
        };
        await app.DbAsync(db =>
        {
            db.BankTransfers.AddRange(
                Transfer(user.Id, a.Id, TransferStatuses.Confirmed, 3),
                Transfer(user.Id, a.Id, TransferStatuses.Confirmed, 2), // the same person again
                Transfer(b.Id, user.Id, TransferStatuses.Confirmed, 30),
                Transfer(user.Id, c.Id, TransferStatuses.Failed, 1), // never settled
                Transfer(c.Id, user.Id, TransferStatuses.Confirmed, 400)); // over a year ago
            return db.SaveChangesAsync();
        });

        var score = (await user.GetAsync<MyTrustDto>($"{Base}/me")).Score;
        Assert.Equal((2, 2m, 2), (score.Settlements, score.HistoryPoints, score.Score));
    }

    [Fact]
    public async Task The_recheck_confirms_accounts_still_granted_and_lets_revoked_ones_decay()
    {
        var user = await app.CreateUserAsync("Recheck Person");
        var (kept, revoked) = (NewCode(), NewCode());
        app.Platforms.Accounts[kept] = new StubPlatforms.Account("li-" + kept, "Kept");
        app.Platforms.Accounts[revoked] = new StubPlatforms.Account("x-" + revoked, "gone", "blue");
        await LinkAsync(user, TrustPlatforms.LinkedIn, kept);
        await LinkAsync(user, TrustPlatforms.X, revoked);
        app.Platforms.Revoked["at-" + revoked] = true;

        // Both were last confirmed 100 days ago.
        var longAgo = DateTimeOffset.UtcNow.AddDays(-100);
        await app.DbAsync(db => db.TrustLinks.Where(l => l.UserId == user.Id).ExecuteUpdateAsync(s => s.SetProperty(l => l.LastConfirmedAt, longAgo)));

        await using (var scope = app.Services.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<TrustService>().RecheckAllAsync(CancellationToken.None);

        var links = await app.DbAsync(db => db.TrustLinks.Where(l => l.UserId == user.Id).ToListAsync());
        Assert.True(links.Single(l => l.Platform == TrustPlatforms.LinkedIn).LastConfirmedAt > DateTimeOffset.UtcNow.AddMinutes(-5));
        var x = links.Single(l => l.Platform == TrustPlatforms.X);
        Assert.Equal((null, null), (x.AccessTokenProtected, x.RefreshTokenProtected));

        // LinkedIn counts in full again; X Premium (revoked, 100 days) counts half in tier 2, but LinkedIn is better there.
        var me = await user.GetAsync<MyTrustDto>($"{Base}/me");
        Assert.Equal(30, me.Score.Score);
        Assert.Equal(0.5m, me.Links.Single(l => l.Platform == TrustPlatforms.X).Credit);
    }

    static ClerkExternalAccount SignIn(string provider, string id, string email) =>
        new("eac_" + id, provider, id, email, null, new ClerkVerification("verified"));

    /// <summary>Makes the next request re-read the user's profile from Clerk.</summary>
    Task ResyncAsync(TestUser user) =>
        app.DbAsync(db => db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.ProfileSyncedAt, DateTimeOffset.MinValue)));

    [Fact]
    public async Task Accounts_people_sign_in_with_count_on_their_own_and_go_when_removed_from_their_sign_in()
    {
        var googleId = "g-" + NewCode();
        var phone = $"+26377{Random.Shared.Next(1000000, 9999999)}";
        var user = await app.CreateUserAsync("Gmail Signer", phone: phone, signIns:
        [
            SignIn("oauth_google", googleId, "signer@gmail.com"),
            // Not verified by the provider: doesn't count.
            new ClerkExternalAccount("eac_x", "oauth_microsoft", "m-" + NewCode(), "x@outlook.com", null, new ClerkVerification("unverified"))
        ]);

        var me = await user.GetAsync<MyTrustDto>($"{Base}/me");
        Assert.Equal(20, me.Score.Score);
        var google = me.Links.Single(l => l.Platform == TrustPlatforms.Google);
        Assert.Equal((true, true, 3, "signer@gmail.com", 1m), (google.Linked, google.ViaSignIn, google.Tier, google.Handle, google.Credit));
        Assert.True(me.Links.Single(l => l.Platform == TrustPlatforms.Phone).ViaSignIn);
        Assert.DoesNotContain(me.Links, l => l.Platform == TrustPlatforms.Microsoft);
        Assert.Equal([TrustPlatforms.Google, TrustPlatforms.Phone], me.Score.Tiers.Single(t => t.Tier == 3).Platforms.Order());

        // It's kept up to date by signing in, so it can't be unlinked here.
        using (var refused = await user.Http.DeleteAsync($"{Base}/links/{TrustPlatforms.Google}"))
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        // Removed from their sign-in: it stops counting at the next profile sync.
        var clerk = app.Clerk.GetUser(user.ClerkId);
        app.Clerk.AddUser(clerk with { ExternalAccounts = [] });
        await ResyncAsync(user);
        me = await user.GetAsync<MyTrustDto>($"{Base}/me");
        Assert.DoesNotContain(me.Links, l => l.Platform == TrustPlatforms.Google);
        Assert.Equal([TrustPlatforms.Phone], me.Score.Tiers.Single(t => t.Tier == 3).Platforms);
    }

    [Fact]
    public async Task Checking_again_reads_a_sign_in_account_added_since_the_last_profile_sync()
    {
        var user = await app.CreateUserAsync("Late Google Adder");
        Assert.Equal(0, (await user.GetAsync<MyTrustDto>($"{Base}/me")).Score.Score);

        // They add Google to their sign-in in Clerk; the profile isn't due a sync for hours.
        var clerk = app.Clerk.GetUser(user.ClerkId);
        app.Clerk.AddUser(clerk with { ExternalAccounts = [SignIn("oauth_google", "g-" + NewCode(), "late@gmail.com")] });
        Assert.DoesNotContain((await user.GetAsync<MyTrustDto>($"{Base}/me")).Links, l => l.Platform == TrustPlatforms.Google);

        var checkedAgain = await user.PostAsync<MyTrustDto>($"{Base}/refresh", new { });
        Assert.Equal(20, checkedAgain.Score.Score);
        Assert.True(checkedAgain.Links.Single(l => l.Platform == TrustPlatforms.Google).ViaSignIn);
    }

    [Fact]
    public async Task Signing_in_with_LinkedIn_counts_as_professional_and_a_sign_in_account_backs_only_one_Ndeipi_account()
    {
        var linkedInId = "li-" + NewCode();
        var first = await app.CreateUserAsync("LinkedIn Signer", signIns: [SignIn("oauth_linkedin_oidc", linkedInId, "pro@example.test")]);
        var mine = await first.GetAsync<MyTrustDto>($"{Base}/me");
        Assert.Equal((30, true), (mine.Score.Score, mine.Links.Single(l => l.Platform == TrustPlatforms.LinkedIn).ViaSignIn));

        var second = await app.CreateUserAsync("Same LinkedIn", signIns: [SignIn("oauth_linkedin_oidc", linkedInId, "pro@example.test")]);
        Assert.Equal(0, (await second.GetAsync<MyTrustDto>($"{Base}/me")).Score.Score);
    }

    [Fact]
    public async Task Work_approved_by_different_clients_in_Gigs_earns_work_record_points()
    {
        Assert.Equal((100, 15m), TrustMath.Compute(true, [Link(TrustPlatforms.LinkedIn, 1)], 10, Now, workReferences: 40) is var r ? (r.Score, r.WorkPoints) : default);

        var worker = await app.CreateUserAsync("Skilled Worker");
        var clients = new List<TestUser>();
        for (var i = 0; i < 4; i++)
            clients.Add(await app.CreateUserAsync($"Client {i}"));
        var now = DateTimeOffset.UtcNow;
        Gig Done(TestUser client, int? stars, string status = GigStatuses.Completed, int daysAgo = 5) => new()
        {
            Id = Guid.NewGuid(), ClientId = client.Id, WorkerId = worker.Id, Title = "Fix a roof", Skill = "construction", TokenSymbol = "NMX",
            Status = status, Budget = 10, WorkerStars = stars, CreatedAt = now.AddDays(-daysAgo - 1), UpdatedAt = now.AddDays(-daysAgo), CompletedAt = now.AddDays(-daysAgo)
        };
        await app.DbAsync(db =>
        {
            db.Gigs.AddRange(
                Done(clients[0], 5),
                Done(clients[0], 4), // the same client again
                Done(clients[1], null), // approved, not rated yet
                Done(clients[2], 2), // rated badly
                Done(clients[3], null, GigStatuses.Assigned), // not done
                Done(clients[3], 5, daysAgo: 800)); // too long ago
            return db.SaveChangesAsync();
        });

        var score = (await clients[0].GetAsync<TrustScoreDto>($"{Base}/{worker.Id}"));
        Assert.Equal((2, 2m, 2), (score.WorkReferences, score.WorkPoints, score.Score));
    }
}
