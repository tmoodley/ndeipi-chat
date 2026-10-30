using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Lending;
using NdeipiChat.Api.Rwa;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Ndeipi Lending against the RWA Lending Platform Portal SRS (Platform &amp; Shared tab): the arithmetic
/// (SC-VLT-02, SC-LN-02, SC-FEE-01, the waterfall, health), then the lifecycle end to end with the gates
/// that guard it (REG-01, REG-05, PLT-CMP, PLT-ORC-02, SC-FAC-01, SC-LN-01, SC-DEF-01, PLT-AUD-01).
/// </summary>
public sealed class LendingTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = LendingContract.BasePath;

    sealed record Cast(TestUser Platform, TestUser Broker, TestUser Developer, TestUser Trustee, TestUser Servicer, TestUser Inspector, TestUser Valuer);

    static string Email(TestUser user) => $"{user.Name.Replace(" ", ".").ToLowerInvariant()}@example.test";

    static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    async Task<TestUser> PersonAsync(string role, bool kyc = true, params string[] roles)
    {
        var name = $"{role} {Guid.NewGuid():N}"[..(role.Length + 9)];
        var user = await app.CreateUserAsync(name, $"{name.Replace(" ", ".").ToLowerInvariant()}@example.test", roles: roles);
        if (kyc)
            await app.DbAsync(db =>
            {
                db.BankingProfiles.Add(new BankingProfile
                {
                    UserId = user.Id, BridgeCustomerId = $"cust_{user.Id:N}", KycLinkId = $"kyc_{user.Id:N}", KycStatus = BankingProfile.Approved,
                    TosStatus = BankingProfile.Approved, UpdatedAt = DateTimeOffset.UtcNow
                });
                return db.SaveChangesAsync();
            });
        return user;
    }

    async Task<Cast> CastAsync() => new(
        await PersonAsync("Platform", true, FinanceContract.AdminRole), await PersonAsync("Broker"), await PersonAsync("Developer"), await PersonAsync("Trustee"),
        await PersonAsync("Servicer"), await PersonAsync("Inspector"), await PersonAsync("Valuer"));

    static LendingTermsRequest Terms(decimal? maxHolding = null, string? blocked = null, string basis = LendingOfferingBases.RegS, decimal scheduled = 5_000m) => new(
        "Test Villas", "Negril, Jamaica", basis, 40_000m, 10_000m, 800, 1400, 100, 6000, 150, 100, 1_000m, scheduled, maxHolding, blocked, null,
        [new(20_000m, "Land"), new(15_000m, "Structure")]);

    static Task<LendingDealDto> Act(TestUser who, Guid deal, string action, object? body = null) => who.PostAsync<LendingDealDto>($"{Base}/deals/{deal}/{action}", body ?? new { });

    static async Task<HttpResponseMessage> Try(TestUser who, Guid deal, string action, object? body = null) =>
        await who.Http.PostAsJsonAsync($"{Base}/deals/{deal}/{action}", body ?? new { }, ContractJson.Options);

    static Task<LendingDealDto> Move(TestUser who, Guid deal, string to, string? reason = null) => Act(who, deal, "transition", new LendingTransitionRequest(to, reason));

    static async Task Profile(TestUser compliance, TestUser who, string investorClass = LendingInvestorClasses.RegS, string country = "CA", bool kyb = false, bool frozen = false)
    {
        using var response = await compliance.Http.PutAsJsonAsync($"{Base}/compliance/profiles",
            new LendingProfileRequest(Email(who), investorClass, country, kyb, frozen, "Checked"), ContractJson.Options);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>A deal with everyone appointed, in Draft.</summary>
    async Task<(Cast Cast, LendingDealDto Deal)> DraftAsync(LendingTermsRequest? terms = null)
    {
        var cast = await CastAsync();
        var deal = await cast.Broker.PostAsync<LendingDealDto>($"{Base}/deals", terms ?? Terms());
        foreach (var (role, who) in new[] { (LendingRoles.Developer, cast.Developer), (LendingRoles.Trustee, cast.Trustee), (LendingRoles.Servicer, cast.Servicer), (LendingRoles.Inspector, cast.Inspector), (LendingRoles.Valuer, cast.Valuer) })
            await Act(cast.Broker, deal.Id, "roles", new LendingAppointRequest(role, Email(who)));
        return (cast, deal);
    }

    /// <summary>A deal whose pool is open: KYB passed, approved, and the land charge attested.</summary>
    async Task<(Cast Cast, LendingDealDto Deal)> OpenAsync(LendingTermsRequest? terms = null)
    {
        var (cast, deal) = await DraftAsync(terms);
        await Profile(cast.Platform, cast.Developer, kyb: true);
        await Act(cast.Broker, deal.Id, "submit");
        await Move(cast.Platform, deal.Id, LendingStates.Approved);
        await Act(cast.Trustee, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.LandCharge, Hash("nla-charge"), null, null));
        return (cast, await Move(cast.Platform, deal.Id, LendingStates.Funding));
    }

    /// <summary>A live loan, both tranches full, collateral locked, the agreement on record and a valuation signed.</summary>
    async Task<(Cast Cast, LendingDealDto Deal, TestUser Senior, TestUser Junior)> ActiveAsync()
    {
        var (cast, deal) = await OpenAsync();
        var senior = await PersonAsync("Senior");
        var junior = await PersonAsync("Junior");
        await Profile(cast.Platform, senior);
        await Profile(cast.Platform, junior);
        await Act(senior, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Senior, 40_000m, Guid.NewGuid()));
        await Act(junior, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Junior, 10_000m, Guid.NewGuid()));
        await Move(cast.Platform, deal.Id, LendingStates.Active);
        await Act(cast.Developer, deal.Id, "collateral/lock");
        await Act(cast.Broker, deal.Id, "documents", new LendingDocumentRequest("Loan agreement", LendingDocumentKinds.SignedLoanAgreement, Hash("agreement")));
        await Act(cast.Valuer, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.Valuation, Hash("appraisal"), 100_000m, null));
        return (cast, await cast.Platform.GetAsync<LendingDealDto>($"{Base}/deals/{deal.Id}"), senior, junior);
    }

    static Task<decimal> Balance(TestUser who) => who.GetAsync<LendingMeDto>($"{Base}/me").ContinueWith(t => t.Result.SimBalanceUsd);

    // ------------------------------------------------------------------ the arithmetic

    [Fact]
    public void The_waterfall_pays_in_the_SRS_order_and_stops_when_the_cash_does()
    {
        var owed = new WaterfallInput(0m, 10m, 20m, 30m, 500m, 40m, 50m, 0m, 60m, false);

        Assert.Equal(new WaterfallResult(10m, 20m, 30m, 40m, 0m, 50m, 60m, 90m), LendingEngine.RunWaterfall(owed with { Cash = 300m }));
        Assert.Equal(new WaterfallResult(10m, 5m, 0m, 0m, 0m, 0m, 0m, 0m), LendingEngine.RunWaterfall(owed with { Cash = 15m }));
        Assert.Equal(new WaterfallResult(10m, 20m, 30m, 15m, 0m, 0m, 0m, 0m), LendingEngine.RunWaterfall(owed with { Cash = 75m }));
        Assert.Equal(new WaterfallResult(10m, 20m, 30m, 40m, 0m, 50m, 5m, 0m), LendingEngine.RunWaterfall(owed with { Cash = 155m }));
        // Scheduled senior principal never exceeds what's outstanding; the reserve only tops up.
        Assert.Equal(7m, LendingEngine.RunWaterfall(owed with { Cash = 300m, SeniorPrincipalOutstanding = 7m }).SeniorPrincipal);
        Assert.Equal(10m, LendingEngine.RunWaterfall(owed with { Cash = 300m, ReserveBalance = 50m }).Reserve);
    }

    [Fact]
    public void In_default_senior_principal_is_swept_before_junior_principal()
    {
        var owed = new WaterfallInput(1_000m, 0m, 0m, 100m, 800m, 0m, 500m, 0m, 50m, true);

        var paid = LendingEngine.RunWaterfall(owed);

        Assert.Equal((100m, 700m, 200m, 0m, 0m), (paid.SeniorPrincipal, paid.SeniorPrincipalSweep, paid.JuniorPrincipal, paid.Reserve, paid.Residual));
        var enough = LendingEngine.RunWaterfall(owed with { Cash = 2_000m });
        Assert.Equal((500m, 50m, 650m), (enough.JuniorPrincipal, enough.Reserve, enough.Residual));
    }

    [Fact]
    public void Losses_hit_junior_first_and_senior_only_after_junior_is_at_zero()
    {
        Assert.Equal((60m, 0m), LendingEngine.ApplyLoss(60m, 100m, 400m));
        Assert.Equal((100m, 0m), LendingEngine.ApplyLoss(100m, 100m, 400m));
        Assert.Equal((100m, 50m), LendingEngine.ApplyLoss(150m, 100m, 400m));
        Assert.Equal((100m, 400m), LendingEngine.ApplyLoss(900m, 100m, 400m));
        Assert.Equal(0.4m, LendingEngine.SharePrice(100m, 60m));
        Assert.Equal(1m, LendingEngine.SharePrice(400m, 0m));
        Assert.Equal(0m, LendingEngine.SharePrice(100m, 150m));
    }

    [Fact]
    public void Interest_accrues_by_the_second_on_drawn_principal_only()
    {
        Assert.Equal(8_000m, LendingEngine.Accrue(100_000m, 800, LendingEngine.SecondsPerYear));
        Assert.Equal(100_000m * 0.08m / LendingEngine.SecondsPerYear, LendingEngine.Accrue(100_000m, 800, 1));
        Assert.Equal(0m, LendingEngine.Accrue(0m, 800, LendingEngine.SecondsPerYear));
        Assert.Equal(0m, LendingEngine.Accrue(100_000m, 800, 0));
    }

    [Fact]
    public void Health_is_max_LTV_over_current_LTV_and_a_valuation_goes_stale_after_12_months()
    {
        Assert.Equal(0.5m, LendingEngine.Ltv(50m, 100m));
        Assert.Null(LendingEngine.Ltv(50m, null));
        Assert.Equal(1.2m, LendingEngine.HealthFactor(6000, 50m, 100m));
        Assert.True(LendingEngine.HealthFactor(6000, 70m, 100m) < 1m);
        Assert.Null(LendingEngine.HealthFactor(6000, 0m, 100m));

        var now = new DateTimeOffset(2026, 10, 30, 0, 0, 0, TimeSpan.Zero);
        Assert.False(LendingEngine.ValuationStale(now.AddMonths(-12), now));
        Assert.True(LendingEngine.ValuationStale(now.AddMonths(-12).AddDays(-1), now));
        Assert.True(LendingEngine.ValuationStale(null, now));
    }

    [Fact]
    public void Fees_come_off_at_source_and_draws_split_across_the_tranches()
    {
        Assert.Equal((300m, 200m, 19_500m), LendingEngine.SplitFees(20_000m, 150, 100));
        Assert.Equal((16_000m, 4_000m), LendingEngine.Allocate(20_000m, 40_000m, 10_000m));
        var (senior, junior) = LendingEngine.Allocate(100m, 1m, 2m);
        Assert.Equal(100m, senior + junior);
    }

    [Fact]
    public void A_draw_lists_every_reason_it_is_blocked()
    {
        var now = DateTimeOffset.UtcNow;
        var ok = new DrawCheck(LendingStates.Active, true, false, true, 2, true, false, 10m, 10m, 20m, 100m, 100m, now, 6000, now);

        Assert.Empty(LendingEngine.DrawBlockers(ok));
        Assert.Contains("frozen", LendingEngine.DrawBlockers(ok with { State = LendingStates.Default })[0]);
        Assert.Contains("isn't locked", LendingEngine.DrawBlockers(ok with { CollateralLocked = false })[0]);
        Assert.Contains("loan agreement", LendingEngine.DrawBlockers(ok with { SignedLoanAgreementOnRecord = false })[0]);
        Assert.Contains("milestone", LendingEngine.DrawBlockers(ok with { MilestoneAttested = false })[0]);
        Assert.Empty(LendingEngine.DrawBlockers(ok with { DrawNumber = 1, MilestoneAttested = false }));
        Assert.Contains("12 months", LendingEngine.DrawBlockers(ok with { ValuedAt = now.AddMonths(-13) })[0]);
        Assert.Contains("LTV", LendingEngine.DrawBlockers(ok with { Amount = 60m, DrawnToDate = 10m, Funded = 1000m })[0]);
        Assert.Contains("funded", LendingEngine.DrawBlockers(ok with { Funded = 15m })[0]);
    }

    // ------------------------------------------------------------------ access and the gates

    [Fact]
    public async Task Anyone_signed_in_can_open_it_and_money_stays_simulated()
    {
        var anyone = await app.CreateUserAsync("Lending Member");

        var me = await anyone.GetAsync<LendingMeDto>($"{Base}/me");
        var manifest = await anyone.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);
        var deals = await anyone.GetAsync<List<LendingDealSummaryDto>>($"{Base}/deals");
        using var signedOut = await app.CreateClient().GetAsync($"{Base}/me");

        Assert.Contains(manifest.Apps, a => a.Id == LendingContract.BrokerAppId);
        Assert.Equal((LendingModes.Simulated, LendingContract.SimulatedNotice, LendingInvestorClasses.Unclassified), (me.Mode, me.Notice, me.InvestorClass));
        Assert.DoesNotContain(deals, d => d.State == LendingStates.Draft);
        Assert.Equal(HttpStatusCode.Unauthorized, signedOut.StatusCode);
    }

    [Fact]
    public async Task People_can_only_be_appointed_by_an_email_they_have_verified()
    {
        var (cast, deal) = await DraftAsync();
        var unverified = await app.CreateUserAsync("Unverified Email", "unverified.email@example.test", verified: false);

        using var refused = await Try(cast.Broker, deal.Id, "roles", new LendingAppointRequest(LendingRoles.Valuer, "unverified.email@example.test"));

        Assert.Contains("No one", await ProblemAsync(refused));
        Assert.DoesNotContain((await cast.Broker.GetAsync<LendingDealDto>($"{Base}/deals/{deal.Id}")).Participants, p => p.UserId == unverified.Id);
    }

    [Fact]
    public async Task With_the_gate_off_nothing_can_change()
    {
        var buyer = await PersonAsync("Gate Off");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == buyer.Id);
        var off = new LendingService(db, scope.ServiceProvider.GetRequiredService<RwaService>(), Options.Create(new LendingOptions()), TimeProvider.System);

        var refused = await Assert.ThrowsAsync<NdeipiChat.Api.Chat.ChatRejectedException>(() => off.CreateAsync(user, Terms(), CancellationToken.None));

        Assert.Equal(LendingModes.Off, new LendingOptions().Mode);
        Assert.Contains("switched off", refused.Message);
        Assert.Equal(LendingModes.Off, (await off.MeAsync(user, CancellationToken.None)).Mode);
    }

    [Fact]
    public async Task A_deal_is_private_until_its_pool_opens_and_needs_KYB_and_the_land_charge_first()
    {
        var (cast, deal) = await DraftAsync();
        var stranger = await PersonAsync("Stranger");

        using var hidden = await stranger.Http.GetAsync($"{Base}/deals/{deal.Id}");
        using var notSubmitted = await Try(cast.Platform, deal.Id, "transition", new LendingTransitionRequest(LendingStates.Approved, null));
        await Act(cast.Broker, deal.Id, "submit");
        using var noKyb = await Try(cast.Platform, deal.Id, "transition", new LendingTransitionRequest(LendingStates.Approved, null));
        using var brokerApproves = await Try(cast.Broker, deal.Id, "transition", new LendingTransitionRequest(LendingStates.Approved, null));
        await Profile(cast.Platform, cast.Developer, kyb: true);
        await Move(cast.Platform, deal.Id, LendingStates.Approved);
        using var noCharge = await Try(cast.Platform, deal.Id, "transition", new LendingTransitionRequest(LendingStates.Funding, null));
        using var wrongSigner = await Try(cast.Valuer, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.LandCharge, Hash("x"), null, null));
        using var noHash = await Try(cast.Trustee, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.LandCharge, "not-a-hash", null, null));
        await Act(cast.Trustee, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.LandCharge, Hash("nla-charge"), null, null));
        var open = await Move(cast.Platform, deal.Id, LendingStates.Funding);

        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Contains("submit", await ProblemAsync(notSubmitted));
        Assert.Contains("KYB", await ProblemAsync(noKyb));
        Assert.Contains("underwriter", await ProblemAsync(brokerApproves));
        Assert.Contains("REG-05", await ProblemAsync(noCharge));
        Assert.Contains("trustee", await ProblemAsync(wrongSigner));
        Assert.Contains("SHA-256", await ProblemAsync(noHash));
        Assert.Equal(LendingStates.Funding, open.State);
        Assert.Contains(await stranger.GetAsync<List<LendingDealSummaryDto>>($"{Base}/deals"), d => d.Id == deal.Id);
    }

    [Fact]
    public async Task The_servicer_and_trustee_must_be_two_people_and_the_developer_cannot_check_their_own_work()
    {
        var (cast, deal) = await DraftAsync();

        using var both = await Try(cast.Broker, deal.Id, "roles", new LendingAppointRequest(LendingRoles.Trustee, Email(cast.Servicer)));
        using var selfInspect = await Try(cast.Broker, deal.Id, "roles", new LendingAppointRequest(LendingRoles.Inspector, Email(cast.Developer)));
        using var nobody = await Try(cast.Broker, deal.Id, "roles", new LendingAppointRequest(LendingRoles.Valuer, "nobody@example.test"));
        using var notBroker = await Try(cast.Valuer, deal.Id, "roles", new LendingAppointRequest(LendingRoles.Valuer, Email(cast.Valuer)));

        Assert.Contains("different people", await ProblemAsync(both));
        Assert.Contains("checks their work", await ProblemAsync(selfInspect));
        Assert.Contains("No one", await ProblemAsync(nobody));
        Assert.Contains("broker or an underwriter", await ProblemAsync(notBroker));
    }

    [Fact]
    public async Task Terms_are_fixed_once_the_pool_opens()
    {
        var (cast, deal) = await DraftAsync();
        using var draftChange = await cast.Broker.Http.PutAsJsonAsync($"{Base}/deals/{deal.Id}/terms", Terms() with { Name = "Renamed" }, ContractJson.Options);
        await Profile(cast.Platform, cast.Developer, kyb: true);
        await Act(cast.Broker, deal.Id, "submit");
        await Move(cast.Platform, deal.Id, LendingStates.Approved);
        await Act(cast.Trustee, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.LandCharge, Hash("charge"), null, null));
        await Move(cast.Platform, deal.Id, LendingStates.Funding);

        using var late = await cast.Platform.Http.PutAsJsonAsync($"{Base}/deals/{deal.Id}/terms", Terms() with { SeniorAprBps = 1 }, ContractJson.Options);
        using var lateRole = await Try(cast.Broker, deal.Id, "roles", new LendingAppointRequest(LendingRoles.Valuer, Email(cast.Inspector)));

        Assert.True(draftChange.IsSuccessStatusCode);
        Assert.Contains("terms are fixed", await ProblemAsync(late));
        Assert.Contains("fixed", await ProblemAsync(lateRole));
        var now = await cast.Platform.GetAsync<LendingDealDto>($"{Base}/deals/{deal.Id}");
        Assert.Equal(("Renamed", 800), (now.Name, now.Terms.SeniorAprBps));
    }

    [Fact]
    public async Task Lending_follows_the_offering_basis_country_blocks_holding_limits_and_freezes()
    {
        var (cast, deal) = await OpenAsync(Terms(maxHolding: 5_000m, blocked: "US,IR"));
        var unverified = await PersonAsync("Unverified", kyc: false);
        var unclassified = await PersonAsync("Unclassified");
        var retail = await PersonAsync("Retail");
        var american = await PersonAsync("American");
        var frozen = await PersonAsync("Frozen");
        var lender = await PersonAsync("Lender");
        await Profile(cast.Platform, retail, LendingInvestorClasses.Retail);
        await Profile(cast.Platform, american, country: "us");
        await Profile(cast.Platform, frozen, frozen: true);
        await Profile(cast.Platform, lender);
        LendingDepositRequest Ten() => new(LendingTranches.Junior, 1_000m, Guid.NewGuid());

        using var noKyc = await Try(unverified, deal.Id, "deposits", Ten());
        using var noClass = await Try(unclassified, deal.Id, "deposits", Ten());
        using var wrongClass = await Try(retail, deal.Id, "deposits", Ten());
        using var blocked = await Try(american, deal.Id, "deposits", Ten());
        using var isFrozen = await Try(frozen, deal.Id, "deposits", Ten());
        var requestId = Guid.NewGuid();
        await Act(lender, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Junior, 4_000m, requestId));
        var retried = await Act(lender, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Junior, 4_000m, requestId));
        using var overLimit = await Try(lender, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Senior, 2_000m, Guid.NewGuid()));

        Assert.Contains("Verify your identity", await ProblemAsync(noKyc));
        Assert.Contains("classified", await ProblemAsync(noClass));
        Assert.Contains("doesn't admit", await ProblemAsync(wrongClass));
        Assert.Contains("your country", await ProblemAsync(blocked));
        Assert.Contains("frozen", await ProblemAsync(isFrozen));
        Assert.Contains("limits each lender", await ProblemAsync(overLimit));
        Assert.Equal(4_000m, retried.Tranches.Single(t => t.Tranche == LendingTranches.Junior).FundedUsd);
        Assert.Equal(46_000m, await Balance(lender));
    }

    [Fact]
    public async Task The_pool_goes_active_only_when_both_tranches_are_full()
    {
        var (cast, deal) = await OpenAsync();

        using var empty = await Try(cast.Platform, deal.Id, "transition", new LendingTransitionRequest(LendingStates.Active, null));

        Assert.Contains("fill to target", await ProblemAsync(empty));
    }

    // ------------------------------------------------------------------ the loan

    [Fact]
    public async Task A_draw_needs_collateral_the_agreement_and_after_the_first_a_milestone_and_pays_fees_at_source()
    {
        var (cast, deal) = await OpenAsync();
        var senior = await PersonAsync("Senior");
        var junior = await PersonAsync("Junior");
        await Profile(cast.Platform, senior);
        await Profile(cast.Platform, junior);
        await Act(senior, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Senior, 40_000m, Guid.NewGuid()));
        await Act(junior, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Junior, 10_000m, Guid.NewGuid()));
        await Move(cast.Platform, deal.Id, LendingStates.Active);

        using var unlocked = await Try(cast.Developer, deal.Id, "draws");
        await Act(cast.Developer, deal.Id, "collateral/lock");
        using var noAgreement = await Try(cast.Developer, deal.Id, "draws");
        await Act(cast.Broker, deal.Id, "documents", new LendingDocumentRequest("Loan agreement", LendingDocumentKinds.SignedLoanAgreement, Hash("v1")));
        using var noValuation = await Try(cast.Developer, deal.Id, "draws");
        await Act(cast.Valuer, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.Valuation, Hash("appraisal"), 100_000m, null));
        using var notDeveloper = await Try(cast.Broker, deal.Id, "draws");
        var brokerBefore = await Balance(cast.Broker);
        var developerBefore = await Balance(cast.Developer);

        var first = await Act(cast.Developer, deal.Id, "draws");
        using var noMilestone = await Try(cast.Developer, deal.Id, "draws");
        await Act(cast.Inspector, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.Milestone, Hash("qs-cert"), null, 2));
        var second = await Act(cast.Developer, deal.Id, "draws");
        using var noMore = await Try(cast.Developer, deal.Id, "draws");

        Assert.Contains("isn't locked", await ProblemAsync(unlocked));
        Assert.Contains("loan agreement", await ProblemAsync(noAgreement));
        Assert.Contains("valuation", await ProblemAsync(noValuation));
        Assert.Contains("project developer", await ProblemAsync(notDeveloper));
        Assert.Contains("milestone", await ProblemAsync(noMilestone));
        Assert.Contains("Every planned drawdown", await ProblemAsync(noMore));

        var draw = first.Draws[0];
        Assert.Equal((300m, 200m, 19_500m), (draw.BrokerFeeUsd, draw.PlatformFeeUsd, draw.NetUsd));
        Assert.Equal(20_000m, first.DrawnPrincipalUsd);
        Assert.Equal(16_000m, first.Tranches.Single(t => t.Tranche == LendingTranches.Senior).PrincipalOutstandingUsd);
        Assert.Equal(4_000m, first.Tranches.Single(t => t.Tranche == LendingTranches.Junior).PrincipalOutstandingUsd);
        Assert.Equal(brokerBefore + 300m + 225m, await Balance(cast.Broker));
        Assert.Equal(developerBefore + 19_500m + 14_625m, await Balance(cast.Developer));
        Assert.Equal(35_000m, second.DrawnPrincipalUsd);
        Assert.Equal(0.35m, second.Ltv);
        Assert.Equal(1.7143m, Math.Round(second.HealthFactor!.Value, 4));
        Assert.False(second.MarginEvent);
    }

    [Fact]
    public async Task A_stale_valuation_blocks_borrowing_and_a_low_one_is_a_margin_event()
    {
        var (cast, deal, _, _) = await ActiveAsync();
        await app.DbAsync(db => db.LendingDeals.Where(d => d.Id == deal.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.ValuedAt, DateTimeOffset.UtcNow.AddMonths(-13))));

        var stale = await cast.Developer.GetAsync<LendingDealDto>($"{Base}/deals/{deal.Id}");
        using var refused = await Try(cast.Developer, deal.Id, "draws");
        await Act(cast.Valuer, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.Valuation, Hash("new"), 100_000m, null));
        await Act(cast.Developer, deal.Id, "draws");
        var low = await Act(cast.Valuer, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.Valuation, Hash("low"), 30_000m, null));

        Assert.True(stale.ValuationStale);
        Assert.Contains("12 months", await ProblemAsync(refused));
        Assert.True(low.MarginEvent);
        Assert.True(low.HealthFactor < 1m);
    }

    [Fact]
    public async Task Interest_builds_on_what_was_drawn_and_a_payment_runs_the_waterfall_once_per_reference()
    {
        var (cast, deal, senior, junior) = await ActiveAsync();
        await Act(cast.Developer, deal.Id, "draws");
        await app.DbAsync(db => db.LendingDeals.Where(d => d.Id == deal.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.AccruedAt, DateTimeOffset.UtcNow.AddSeconds(-LendingEngine.SecondsPerYear))));

        var year = await cast.Servicer.GetAsync<LendingDealDto>($"{Base}/deals/{deal.Id}");
        var seniorBefore = await Balance(senior);
        var juniorBefore = await Balance(junior);
        var paid = await Act(cast.Servicer, deal.Id, "payments", new LendingPaymentRequest("BANK-1", 2_000m));
        using var again = await Try(cast.Servicer, deal.Id, "payments", new LendingPaymentRequest("BANK-1", 2_000m));
        using var notServicer = await Try(cast.Broker, deal.Id, "payments", new LendingPaymentRequest("BANK-2", 2_000m));

        // A year on $16,000 senior at 8%, $4,000 junior at 14%, and $20,000 at 1% fees.
        Assert.InRange(year.Tranches[0].InterestDueUsd, 1_280m, 1_281m);
        Assert.InRange(year.Tranches[1].InterestDueUsd, 560m, 561m);
        Assert.InRange(year.FeesDueUsd, 200m, 201m);
        // $2,000 pays the fees, then senior interest, then what's left of it goes to senior principal.
        var step = paid.Payments.Single().Paid;
        Assert.Equal(step.Fees + step.SeniorInterest + step.SeniorPrincipal + step.JuniorInterest + step.JuniorPrincipal + step.Reserve + step.Residual, 2_000m);
        Assert.InRange(step.Fees, 200m, 201m);
        Assert.InRange(step.SeniorInterest, 1_280m, 1_281m);
        Assert.True(step.SeniorPrincipal > 0);
        Assert.Equal(0m, step.JuniorInterest);
        Assert.Equal(seniorBefore + step.SeniorInterest + step.SeniorPrincipal, await Balance(senior));
        Assert.Equal(juniorBefore, await Balance(junior));
        Assert.Contains("already been recorded", await ProblemAsync(again));
        Assert.Contains("loan servicer", await ProblemAsync(notServicer));
    }

    [Fact]
    public async Task A_loan_repaid_in_full_releases_the_collateral_and_returns_undrawn_money()
    {
        var (cast, deal, senior, junior) = await ActiveAsync();
        await Act(cast.Developer, deal.Id, "draws");
        await Act(cast.Inspector, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.Milestone, Hash("qs"), null, 2));
        await Act(cast.Developer, deal.Id, "draws");
        var developerBefore = await Balance(cast.Developer);

        // Scheduled senior principal is $5,000 a payment, so the senior $28,000 takes six payments.
        LendingDealDto now = deal;
        for (var i = 1; i <= 6; i++)
            now = await Act(cast.Servicer, deal.Id, "payments", new LendingPaymentRequest($"BANK-{i}", 12_000m));

        Assert.Equal(LendingStates.Repaid, now.State);
        Assert.False(now.CollateralLocked);
        Assert.Equal(0m, now.DrawnPrincipalUsd);
        Assert.Equal(0m, now.ReserveUsd);
        Assert.Equal(1m, now.Tranches[0].SharePrice);
        Assert.True(await Balance(cast.Developer) > developerBefore, "The residual and the reserve go back to the borrower.");
        // Every lender has all $50,000 back: $35,000 repaid and the $15,000 never drawn, plus interest.
        Assert.True(await Balance(senior) >= 50_000m);
        Assert.True(await Balance(junior) >= 50_000m);
        Assert.Contains(await cast.Servicer.GetAsync<List<LendingAuditDto>>($"{Base}/deals/{deal.Id}/audit"), e => e.After == LendingStates.Repaid);
    }

    [Fact]
    public async Task Default_takes_the_servicer_and_trustee_freezes_the_loan_and_write_off_takes_junior_first()
    {
        var (cast, deal, senior, junior) = await ActiveAsync();
        await Act(cast.Developer, deal.Id, "draws");
        using var notGrace = await Try(cast.Servicer, deal.Id, "default-votes", new LendingReasonRequest("Missed"));
        using var noReason = await Try(cast.Servicer, deal.Id, "transition", new LendingTransitionRequest(LendingStates.Grace, " "));
        await Move(cast.Servicer, deal.Id, LendingStates.Grace, "Missed the October payment");
        using var outsider = await Try(cast.Broker, deal.Id, "default-votes", new LendingReasonRequest("Missed"));
        using var drawInGrace = await Try(cast.Developer, deal.Id, "draws");

        var oneSigned = await Act(cast.Servicer, deal.Id, "default-votes", new LendingReasonRequest("Missed payment"));
        var twice = await Act(cast.Servicer, deal.Id, "default-votes", new LendingReasonRequest("Missed payment"));
        var both = await Act(cast.Trustee, deal.Id, "default-votes", new LendingReasonRequest("Confirmed"));
        using var drawInDefault = await Try(cast.Developer, deal.Id, "draws");

        Assert.Contains("in grace", await ProblemAsync(notGrace));
        Assert.Contains("reason", await ProblemAsync(noReason));
        Assert.Contains("servicer and the security trustee", await ProblemAsync(outsider));
        Assert.Contains("frozen", await ProblemAsync(drawInGrace));
        Assert.Equal(LendingStates.Grace, oneSigned.State);
        Assert.Equal(LendingStates.Grace, twice.State);
        Assert.Equal(LendingStates.Default, both.State);
        Assert.True(both.CollateralFrozen);
        Assert.Contains("frozen", await ProblemAsync(drawInDefault));

        // $10,000 recovered: in default it sweeps to senior ahead of junior principal.
        var recovered = await Act(cast.Servicer, deal.Id, "payments", new LendingPaymentRequest("SALE-1", 10_000m));
        var sweep = recovered.Payments.Single().Paid;
        Assert.True(sweep.SeniorPrincipalSweep > 0);
        Assert.Equal(0m, sweep.JuniorPrincipal);

        using var earlyWriteOff = await Try(cast.Trustee, deal.Id, "write-off", new LendingReasonRequest("Done"));
        using var servicerRecovers = await Try(cast.Servicer, deal.Id, "transition", new LendingTransitionRequest(LendingStates.Recovery, null));
        await Move(cast.Trustee, deal.Id, LendingStates.Recovery);
        var written = await Act(cast.Trustee, deal.Id, "write-off", new LendingReasonRequest("Land sold; nothing more to come"));

        Assert.Contains("in recovery", await ProblemAsync(earlyWriteOff));
        Assert.Contains("security trustee", await ProblemAsync(servicerRecovers));
        Assert.Equal(LendingStates.WrittenOff, written.State);
        var (s, j) = (written.Tranches[0], written.Tranches[1]);
        Assert.Equal(4_000m, j.LossUsd);
        Assert.True(s.LossUsd < 8_000m, "The recovery went to senior, so it lost less than its share of the $20,000.");
        Assert.Equal(1m - 4_000m / 10_000m, j.SharePrice);
        Assert.Equal(Math.Round(1m - s.LossUsd / 40_000m, 8), Math.Round(s.SharePrice, 8));
        Assert.True(j.SharePrice < s.SharePrice, "Junior falls further than senior.");
        // Undrawn money goes back: $24,000 senior and $6,000 junior were never drawn.
        Assert.True(await Balance(junior) >= 46_000m - 1m);
        Assert.Equal(4_000m, (await junior.GetAsync<LendingDealDto>($"{Base}/deals/{deal.Id}")).MyPositions.Single().LossUsd);
        Assert.True(await Balance(senior) > 40_000m);
    }

    [Fact]
    public async Task A_cured_grace_period_clears_the_default_signature()
    {
        var (cast, deal, _, _) = await ActiveAsync();
        await Act(cast.Developer, deal.Id, "draws");
        await Move(cast.Servicer, deal.Id, LendingStates.Grace, "Late");
        await Act(cast.Servicer, deal.Id, "default-votes", new LendingReasonRequest("Late"));

        var cured = await Move(cast.Servicer, deal.Id, LendingStates.Active, "Paid");
        await Move(cast.Servicer, deal.Id, LendingStates.Grace, "Late again");
        var trusteeAlone = await Act(cast.Trustee, deal.Id, "default-votes", new LendingReasonRequest("Late again"));

        Assert.Empty(cured.DefaultVotes);
        Assert.Equal(LendingStates.Grace, trusteeAlone.State);
    }

    // ------------------------------------------------------------------ documents and audit

    [Fact]
    public async Task The_data_room_keeps_versions_of_hashes_for_the_people_on_the_deal()
    {
        var (cast, deal) = await DraftAsync();
        var outsider = await PersonAsync("Outsider");

        await Act(cast.Broker, deal.Id, "documents", new LendingDocumentRequest("Title deed", LendingDocumentKinds.TitleDeed, Hash("v1")));
        var two = await Act(cast.Developer, deal.Id, "documents", new LendingDocumentRequest("Title deed", LendingDocumentKinds.TitleDeed, Hash("v2")));
        using var bad = await Try(cast.Broker, deal.Id, "documents", new LendingDocumentRequest("Title deed", LendingDocumentKinds.TitleDeed, "ABC"));
        using var stranger = await Try(outsider, deal.Id, "documents", new LendingDocumentRequest("Mine", LendingDocumentKinds.Other, Hash("x")));

        Assert.Equal([(2, Hash("v2")), (1, Hash("v1"))], two.Documents.Select(d => (d.Version, d.Sha256)));
        Assert.Contains("SHA-256", await ProblemAsync(bad));
        Assert.Contains("people on this deal", await ProblemAsync(stranger));
    }

    [Fact]
    public async Task The_audit_log_records_who_did_what_and_cannot_be_rewritten()
    {
        var (cast, deal) = await OpenAsync();
        var outsider = await PersonAsync("Outsider");

        var log = await cast.Broker.GetAsync<List<LendingAuditDto>>($"{Base}/deals/{deal.Id}/audit");
        using var hidden = await outsider.Http.GetAsync($"{Base}/deals/{deal.Id}/audit");

        Assert.Contains(log, e => e.Action == "deal.state" && e.Before == LendingStates.Draft && e.After == LendingStates.Approved && e.Actor == cast.Platform.Name);
        Assert.Contains(log, e => e.Action == "attest.land_charge" && e.After == Hash("nla-charge"));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);

        var update = await Record.ExceptionAsync(() => app.DbAsync(db => db.LendingAuditEvents.Where(e => e.DealId == deal.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Reason, "rewritten"))));
        var delete = await Record.ExceptionAsync(() => app.DbAsync(db => db.LendingAuditEvents.Where(e => e.DealId == deal.Id).ExecuteDeleteAsync()));

        Assert.Contains("append-only", update?.Message);
        Assert.Contains("append-only", delete?.Message);
        Assert.Equal(log.Count, (await cast.Broker.GetAsync<List<LendingAuditDto>>($"{Base}/deals/{deal.Id}/audit")).Count);
    }

    // ------------------------------------------------------------------ with RWA

    [Fact]
    public async Task The_reference_deal_is_Green_Island_and_villa_buyers_are_told_they_rank_below_lenders()
    {
        var underwriter = await PersonAsync("Reference", true, FinanceContract.AdminRole);
        var broker = await PersonAsync("Not Underwriter");
        using var refused = await broker.Http.PostAsJsonAsync($"{Base}/deals/reference", new { }, ContractJson.Options);
        var before = await underwriter.GetAsync<RwaAssetDetailDto>($"{RwaContract.BasePath}/assets/GI-VILLA-01");

        var deal = await underwriter.PostAsync<LendingDealDto>($"{Base}/deals/reference", new { });
        var again = await underwriter.PostAsync<LendingDealDto>($"{Base}/deals/reference", new { });
        var villa = await underwriter.GetAsync<RwaAssetDetailDto>($"{RwaContract.BasePath}/assets/GI-VILLA-01");
        var condo = await underwriter.GetAsync<RwaAssetDetailDto>($"{RwaContract.BasePath}/assets/GI-CONDO-01");

        Assert.Contains("underwriter", await ProblemAsync(refused));
        Assert.Equal(("GI-LOAN-01", LendingStates.Draft, deal.Id), (deal.Code, deal.State, again.Id));
        Assert.Equal(3, deal.Draws.Count);
        Assert.Equal(villa.Asset.Id, deal.Terms.RwaAssetId);
        Assert.False(before.RanksBelowLenders);
        Assert.True(villa.RanksBelowLenders);
        Assert.False(condo.RanksBelowLenders);
    }

    // ------------------------------------------------------------------ the portals (drafted requirements)

    [Fact]
    public async Task Each_portal_is_its_own_tile_from_one_bundle()
    {
        var anyone = await app.CreateUserAsync("Any Tile");

        var manifest = await anyone.GetAsync<LauncherManifestDto>(LauncherContract.ManifestPath);

        string[] tiles = [LendingContract.BrokerAppId, LendingContract.LenderAppId, LendingContract.BorrowerAppId];
        Assert.All(tiles, id => Assert.Equal("NdeipiChat.SubApps.Lending", manifest.Apps.Single(a => a.Id == id).Assembly));
    }

    [Fact]
    public async Task A_broker_works_a_deal_through_the_pipeline_and_the_underwriter_can_return_it()
    {
        var (cast, deal) = await DraftAsync();
        await Act(cast.Broker, deal.Id, "documents", new LendingDocumentRequest("Title deed", LendingDocumentKinds.TitleDeed, Hash("title")));

        var before = (await cast.Broker.GetAsync<BrokerPipelineDto>($"{Base}/broker/pipeline")).Deals.Single();
        Assert.True(before.Checklist.Single(c => c.Label == "Data room started").Done);
        Assert.True(before.Checklist.Single(c => c.Label == "Trustee, servicer and inspector named").Done);
        Assert.False(before.Checklist.Single(c => c.Label == "Developer passed KYB").Done);

        await Act(cast.Broker, deal.Id, "submit");
        using var locked = await cast.Broker.Http.PutAsJsonAsync($"{Base}/deals/{deal.Id}/terms", Terms() with { Name = "Too late" }, ContractJson.Options);
        Assert.Contains("before submitting", await ProblemAsync(locked));
        Assert.Contains((await cast.Platform.GetAsync<BrokerPipelineDto>($"{Base}/broker/pipeline")).Queue, q => q.Id == deal.Id);
        using var brokerReturns = await Try(cast.Broker, deal.Id, "return", new LendingReasonRequest("No"));
        Assert.Contains("underwriter", await ProblemAsync(brokerReturns));

        var returned = await Act(cast.Platform, deal.Id, "return", new LendingReasonRequest("Fix the drawdown plan"));
        Assert.Equal("Fix the drawdown plan", returned.ReturnReason);
        Assert.Null(returned.SubmittedAt);
        Assert.DoesNotContain((await cast.Platform.GetAsync<BrokerPipelineDto>($"{Base}/broker/pipeline")).Queue, q => q.Id == deal.Id);
        Assert.Contains(await cast.Broker.GetAsync<List<LendingNoticeDto>>($"{Base}/notices"), n => n.Text.Contains("Fix the drawdown plan"));
        using var editable = await cast.Broker.Http.PutAsJsonAsync($"{Base}/deals/{deal.Id}/terms", Terms() with { Name = "Fixed" }, ContractJson.Options);
        Assert.True(editable.IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_broker_sees_the_fee_taken_at_source_on_each_draw()
    {
        var (cast, deal, _, _) = await ActiveAsync();
        await Act(cast.Developer, deal.Id, "draws");
        await Act(cast.Inspector, deal.Id, "attestations", new LendingAttestationRequest(LendingAttestationKinds.Milestone, Hash("qs"), null, 2));
        await Act(cast.Developer, deal.Id, "draws");

        var pipeline = await cast.Broker.GetAsync<BrokerPipelineDto>($"{Base}/broker/pipeline");

        Assert.Equal([300m, 225m], pipeline.Deals.Single().FeePerDraw);
        Assert.Equal(525m, pipeline.FeesEarnedUsd);
    }

    [Fact]
    public async Task A_lender_sees_open_pools_with_the_reason_they_cannot_lend_and_their_portfolio()
    {
        var (cast, deal) = await OpenAsync();
        var newcomer = await PersonAsync("Newcomer");

        var before = await newcomer.GetAsync<LenderPortfolioDto>($"{Base}/lender/portfolio");
        Assert.Contains("classified", before.OpenPools.Single(p => p.Deal.Id == deal.Id).CanLend);
        await Profile(cast.Platform, newcomer);
        await Act(newcomer, deal.Id, "deposits", new LendingDepositRequest(LendingTranches.Junior, 2_000m, Guid.NewGuid()));

        var after = await newcomer.GetAsync<LenderPortfolioDto>($"{Base}/lender/portfolio");

        Assert.Null(after.OpenPools.Single(p => p.Deal.Id == deal.Id).CanLend);
        var position = after.Positions.Single();
        Assert.Equal((LendingTranches.Junior, 2_000m, 1m, 2_000m), (position.Tranche, position.DepositedUsd, position.SharePrice, position.ValueUsd));
        Assert.Equal(2_000m, after.DepositedUsd);
    }

    [Fact]
    public async Task Redemption_is_a_request_then_a_window_and_pays_only_what_the_draw_plan_does_not_need()
    {
        var (cast, deal, senior, _) = await ActiveAsync();
        var requested = await Act(senior, deal.Id, "redemptions", new LendingRedeemRequest(LendingTranches.Senior, 12_000m));
        var early = requested.MyRedemptions!.Single();
        Assert.Equal(LendingRedemptions.Requested, early.Status);

        using var tooSoon = await Try(senior, deal.Id, $"redemptions/{early.Id}/redeem");
        Assert.Contains("window opens", await ProblemAsync(tooSoon));
        using var tooMuch = await Try(senior, deal.Id, "redemptions", new LendingRedeemRequest(LendingTranches.Senior, 28_001m));
        Assert.Contains("more than you have", await ProblemAsync(tooMuch));
        using var stranger = await Try(cast.Broker, deal.Id, "redemptions", new LendingRedeemRequest(LendingTranches.Senior, 1m));
        Assert.Contains("nothing in that tranche", await ProblemAsync(stranger));

        await OpenWindowAsync(deal.Id);
        var before = await Balance(senior);
        var paid = await Act(senior, deal.Id, $"redemptions/{early.Id}/redeem");

        Assert.Equal(before + 12_000m, await Balance(senior));
        Assert.Equal(12_000m, paid.MyPositions.Single().RedeemedUsd);
        using var again = await Try(senior, deal.Id, $"redemptions/{early.Id}/redeem");
        Assert.Contains("has been paid", await ProblemAsync(again));

        // $12,000 of the senior tranche was all the plan didn't need.
        var more = (await Act(senior, deal.Id, "redemptions", new LendingRedeemRequest(LendingTranches.Senior, 1m))).MyRedemptions!.First(r => r.Status == LendingRedemptions.Requested);
        await OpenWindowAsync(deal.Id);
        using var noFreeMoney = await Try(senior, deal.Id, $"redemptions/{more.Id}/redeem");
        Assert.Contains("isn't needed for drawdowns", await ProblemAsync(noFreeMoney));

        // The draws still fund in full.
        Assert.Equal(20_000m, (await Act(cast.Developer, deal.Id, "draws")).DrawnPrincipalUsd);
    }

    Task OpenWindowAsync(Guid dealId) => app.DbAsync(db => db.LendingRedemptions.Where(r => r.DealId == dealId && r.Status == LendingRedemptions.Requested)
        .ExecuteUpdateAsync(s => s.SetProperty(r => r.WindowOpensAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

    [Fact]
    public async Task A_redemption_is_refused_once_the_loan_stops_being_active()
    {
        var (cast, deal, senior, _) = await ActiveAsync();
        var requested = (await Act(senior, deal.Id, "redemptions", new LendingRedeemRequest(LendingTranches.Senior, 1_000m))).MyRedemptions!.Single();
        await OpenWindowAsync(deal.Id);
        await Move(cast.Servicer, deal.Id, LendingStates.Grace, "Late");

        using var refused = await Try(senior, deal.Id, $"redemptions/{requested.Id}/redeem");

        Assert.Contains("only open while the loan is active", await ProblemAsync(refused));
    }

    [Fact]
    public async Task A_borrower_sees_their_loan_asks_for_inspection_and_repays_through_the_waterfall()
    {
        var (cast, deal, senior, _) = await ActiveAsync();
        var first = (await cast.Developer.GetAsync<BorrowerLoansDto>($"{Base}/borrower/loans")).Loans.Single();
        Assert.Equal((1, 0m), (first.NextDraw!.Number, first.DrawnPrincipalUsd));
        Assert.Empty(first.NextDraw.Blockers);
        await Act(cast.Developer, deal.Id, "draws");

        var loans = await cast.Developer.GetAsync<BorrowerLoansDto>($"{Base}/borrower/loans");
        var loan = loans.Loans.Single();
        Assert.True(loans.KybApproved);
        Assert.Equal(20_000m, loan.DrawnPrincipalUsd);
        Assert.Contains("milestone", loan.NextDraw!.Blockers[0]);
        Assert.True(loan.DueNowUsd >= 5_000m, "Due now includes the next scheduled senior principal.");
        Assert.Equal(3m, loan.HealthFactor);

        await Act(cast.Developer, deal.Id, "inspection", new LendingInspectionRequest(2));
        Assert.Contains(await cast.Inspector.GetAsync<List<LendingNoticeDto>>($"{Base}/notices"), n => n.Text.Contains("inspect tranche 2"));
        using var notTheirs = await Try(cast.Broker, deal.Id, "inspection", new LendingInspectionRequest(2));
        Assert.Contains("project developer", await ProblemAsync(notTheirs));

        var developerBefore = await Balance(cast.Developer);
        var seniorBefore = await Balance(senior);
        var repaid = await Act(cast.Developer, deal.Id, "repay", new LendingRepayRequest(3_000m));
        using var tooMuch = await Try(cast.Developer, deal.Id, "repay", new LendingRepayRequest(10_000_000m));
        using var notDeveloper = await Try(cast.Servicer, deal.Id, "repay", new LendingRepayRequest(1m));

        Assert.Equal(developerBefore - 3_000m, await Balance(cast.Developer));
        Assert.True(await Balance(senior) > seniorBefore, "Senior is paid first.");
        Assert.StartsWith("BORROWER-", repaid.Payments.Single().Reference);
        Assert.Contains("doesn't cover", await ProblemAsync(tooMuch));
        Assert.Contains("project developer", await ProblemAsync(notDeveloper));
    }

    [Fact]
    public async Task A_borrower_without_KYB_is_told_and_notices_reach_everyone_on_a_deal()
    {
        var (cast, deal, senior, _) = await ActiveAsync();
        var waiting = await PersonAsync("Waiting");
        Assert.False((await waiting.GetAsync<BorrowerLoansDto>($"{Base}/borrower/loans")).KybApproved);

        await Act(cast.Developer, deal.Id, "draws");
        await Move(cast.Servicer, deal.Id, LendingStates.Grace, "Late");

        var lender = await senior.GetAsync<List<LendingNoticeDto>>($"{Base}/notices");
        Assert.Contains(lender, n => n.Text.Contains("tranche 1 was drawn"));
        Assert.Contains(lender, n => n.Text.Contains("is now Grace"));
        Assert.All(lender, n => Assert.False(n.Read));
        Assert.Contains(await cast.Developer.GetAsync<List<LendingNoticeDto>>($"{Base}/notices"), n => n.Text.Contains("is now Grace"));

        using var read = await senior.Http.PostAsJsonAsync($"{Base}/notices/read", new { }, ContractJson.Options);
        read.EnsureSuccessStatusCode();
        Assert.All(await senior.GetAsync<List<LendingNoticeDto>>($"{Base}/notices"), n => Assert.True(n.Read));
    }
}
