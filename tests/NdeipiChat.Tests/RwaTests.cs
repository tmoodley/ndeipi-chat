using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Rwa;
using NdeipiChat.Contracts;
using NdeipiChat.Tests.Infrastructure;

namespace NdeipiChat.Tests;

/// <summary>
/// Ndeipi RWA phase 1 against the RWA &amp; Time-Stripping SRS: tranche pricing and the calendar
/// (FR-2.1), the KYC gate (FR-1.2), the cooling-off escrow and its refund (FR-1.3), vesting into the
/// Year 1 vault (FR-2.2), oracle-gated NAV accretion (FR-2.4, NFR-2), and the settlement gate that
/// keeps everything simulated.
/// </summary>
public sealed class RwaTests(TestApp app) : IClassFixture<TestApp>
{
    const string Base = RwaContract.BasePath;
    const string Villa = "GI-VILLA-01", Condo = "GI-CONDO-01";

    static async Task<string> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString()!;

    async Task<TestUser> VerifiedAsync(string name, string[]? roles = null)
    {
        var user = await app.CreateUserAsync(name, roles: roles);
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

    static Task<RwaHoldingDto> MintAsync(TestUser user, string asset, string type, int index, Guid? requestId = null) =>
        user.PostAsync<RwaHoldingDto>($"{Base}/presale/mint", new RwaMintRequest(asset, type, index, RwaPaymentMethods.Simulated, requestId ?? Guid.NewGuid()));

    static async Task<HttpResponseMessage> TryMintAsync(TestUser user, string asset, string type, int index, string payment = RwaPaymentMethods.Simulated) =>
        await user.Http.PostAsJsonAsync($"{Base}/presale/mint", new RwaMintRequest(asset, type, index, payment, Guid.NewGuid()), ContractJson.Options);

    /// <summary>Moves a holding's cooling-off end into the past, as if seven days went by.</summary>
    Task EndCoolingOffAsync(Guid holdingId) => app.DbAsync(db =>
        db.RwaHoldings.Where(h => h.Id == holdingId).ExecuteUpdateAsync(s => s.SetProperty(h => h.CoolingOffEndsAt, DateTimeOffset.UtcNow.AddMinutes(-1))));

    [Fact]
    public void Weeks_are_priced_by_season_and_add_up_to_the_year_and_months_cover_every_week_once()
    {
        const decimal unit = 520_000m;

        Assert.Equal(RwaSeasons.Peak, RwaSeasons.Of(1));
        Assert.Equal(RwaSeasons.Peak, RwaSeasons.Of(52));
        Assert.Equal(RwaSeasons.Shoulder, RwaSeasons.Of(20));
        Assert.Equal(RwaSeasons.OffPeak, RwaSeasons.Of(36));
        Assert.True(RwaPricing.Price(unit, RwaIntervals.Week, 5) > RwaPricing.Price(unit, RwaIntervals.Week, 20));
        Assert.True(RwaPricing.Price(unit, RwaIntervals.Week, 20) > RwaPricing.Price(unit, RwaIntervals.Week, 36));
        var weeks = Enumerable.Range(1, 52).Sum(w => RwaPricing.Price(unit, RwaIntervals.Week, w));
        Assert.InRange(weeks, unit - 1m, unit + 1m);
        Assert.Equal(Math.Round(unit / 12, 2), RwaPricing.Price(unit, RwaIntervals.Month, 7));
        Assert.Equal(unit, RwaPricing.Price(unit, RwaIntervals.FullYear, 1));

        var covered = Enumerable.Range(1, 12).SelectMany(m => RwaCalendar.Weeks(RwaIntervals.Month, m)).ToList();
        Assert.Equal(Enumerable.Range(1, 52), covered);
        Assert.Equal((9, 13), RwaCalendar.MonthWeeks(3));
        Assert.Equal(("1/52", "1/12", "1/1"), (RwaContract.Fraction(RwaIntervals.Week), RwaContract.Fraction(RwaIntervals.Month), RwaContract.Fraction(RwaIntervals.FullYear)));
        Assert.False(RwaCalendar.IsValidIndex(RwaIntervals.Week, 53));
        Assert.False(RwaCalendar.IsValidIndex(RwaIntervals.FullYear, 2));
    }

    [Fact]
    public async Task The_catalogue_lists_the_Green_Island_villas_with_their_calendar()
    {
        var user = await app.CreateUserAsync("Rwa Browser");

        var assets = await user.GetAsync<List<RwaAssetDto>>($"{Base}/assets");
        var detail = await user.GetAsync<RwaAssetDetailDto>($"{Base}/assets/{Villa}");
        using var missing = await user.Http.GetAsync($"{Base}/assets/NOPE-01");
        var me = await user.GetAsync<RwaMeDto>($"{Base}/me");

        Assert.Contains(assets, a => a.Code == Villa && a.AssetClass == RwaAssetClasses.ReitVilla && a.TokenStandard == "ERC1155" && a.ConstructionPhase == RwaPhases.LandPresale);
        Assert.Contains(assets, a => a.Code == Condo);
        Assert.Equal(52, detail.Weeks.Count);
        Assert.Equal(12, detail.Months.Count);
        Assert.Equal(detail.Asset.UnitPriceUsd, detail.FullYearPriceUsd);
        Assert.Equal(RwaPricing.Price(detail.Asset.UnitPriceUsd, RwaIntervals.Week, 3), detail.Weeks[2].PriceUsd);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal((RwaModes.Simulated, false, 7, 50_000m), (me.Mode, me.KycVerified, me.CoolingOffDays, me.SimBalanceUsd));
    }

    [Fact]
    public async Task Only_people_who_passed_KYC_can_buy_and_the_check_is_logged()
    {
        var unverified = await app.CreateUserAsync("Rwa Unverified");

        using var refused = await TryMintAsync(unverified, Villa, RwaIntervals.Week, 30);

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Verify your identity", await ProblemAsync(refused));
        var log = await app.DbAsync(db => db.RwaAuditEvents.Where(e => e.UserId == unverified.Id).ToListAsync());
        Assert.Equal("kyc_check", Assert.Single(log).Kind);
        Assert.Empty(await app.DbAsync(db => db.RwaHoldings.Where(h => h.UserId == unverified.Id).ToListAsync()));
    }

    [Fact]
    public async Task A_week_goes_into_a_seven_day_cooling_off_escrow_and_takes_its_month_and_the_year_off_sale()
    {
        var buyer = await VerifiedAsync("Rwa Week Buyer");
        var before = DateTimeOffset.UtcNow;

        var holding = await MintAsync(buyer, Villa, RwaIntervals.Week, 6);

        var price = RwaPricing.Price(520_000m, RwaIntervals.Week, 6);
        Assert.Equal((RwaHoldingStatus.CoolingOff, RwaSeasons.Peak, "1/52", price, price), (holding.Status, holding.Season, holding.Fraction, holding.PrincipalUsd, holding.ValueUsd));
        Assert.True(holding.Simulated);
        Assert.True(holding.TokenId > 0);
        Assert.InRange(holding.CoolingOffEndsAt, before.AddDays(7).AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(7).AddMinutes(1));
        var detail = await buyer.GetAsync<RwaAssetDetailDto>($"{Base}/assets/{Villa}");
        Assert.True(detail.Weeks.Single(w => w.Week == 6).Taken);
        Assert.False(detail.Months.Single(m => m.Month == 2).Available);
        Assert.False(detail.FullYearAvailable);
        var portfolio = await buyer.GetAsync<RwaPortfolioDto>($"{Base}/portfolio");
        Assert.Equal((price, price, 50_000m - price), (portfolio.InvestedUsd, portfolio.InCoolingOffUsd, portfolio.SimBalanceUsd));

        // The month holding week 6, and the full year, can't be bought now.
        var other = await VerifiedAsync("Rwa Month Buyer");
        using var month = await TryMintAsync(other, Villa, RwaIntervals.Month, 2);
        Assert.Contains("has been sold", await ProblemAsync(month));
        using var year = await TryMintAsync(other, Villa, RwaIntervals.FullYear, 1);
        Assert.Equal(HttpStatusCode.BadRequest, year.StatusCode);
    }

    [Fact]
    public async Task A_week_is_sold_once_however_many_people_buy_it_at_the_same_time()
    {
        var buyers = await Task.WhenAll(Enumerable.Range(1, 8).Select(i => VerifiedAsync($"Rwa Racer {i}")));

        var attempts = await Task.WhenAll(buyers.Select(b => TryMintAsync(b, Villa, RwaIntervals.Week, 40)));

        Assert.Single(attempts, r => r.IsSuccessStatusCode);
        Assert.Equal(1, await app.DbAsync(db => db.RwaWeekSlots.CountAsync(s => s.Week == 40 && db.RwaAssets.Any(a => a.Id == s.AssetId && a.Code == Villa))));
        // Everyone who lost has their test balance back untouched.
        var balances = await app.DbAsync(db => db.RwaSimAccounts.Where(a => buyers.Select(b => b.Id).Contains(a.UserId)).Select(a => a.BalanceUsd).ToListAsync());
        Assert.Equal(7, balances.Count(b => b == 50_000m));
        foreach (var r in attempts)
            r.Dispose();
    }

    [Fact]
    public async Task Retrying_a_purchase_with_the_same_request_id_buys_once()
    {
        var buyer = await VerifiedAsync("Rwa Double Tap");
        var requestId = Guid.NewGuid();

        var first = await MintAsync(buyer, Villa, RwaIntervals.Week, 31, requestId);
        var again = await MintAsync(buyer, Villa, RwaIntervals.Week, 31, requestId);

        Assert.Equal(first.Id, again.Id);
        var portfolio = await buyer.GetAsync<RwaPortfolioDto>($"{Base}/portfolio");
        Assert.Single(portfolio.Holdings);
        Assert.Equal(50_000m - first.PrincipalUsd, portfolio.SimBalanceUsd);
    }

    [Fact]
    public async Task Only_simulated_settlement_is_switched_on_and_balances_are_checked()
    {
        var buyer = await VerifiedAsync("Rwa Real Money");

        using var points = await TryMintAsync(buyer, Villa, RwaIntervals.Week, 33, RwaPaymentMethods.Points);
        using var absa = await TryMintAsync(buyer, Villa, RwaIntervals.Week, 33, RwaPaymentMethods.Absa);
        using var tooMuch = await TryMintAsync(buyer, Condo, RwaIntervals.FullYear, 1);
        using var badWeek = await TryMintAsync(buyer, Villa, RwaIntervals.Week, 53);

        Assert.Contains("Only simulated settlement", await ProblemAsync(points));
        Assert.Contains("Only simulated settlement", await ProblemAsync(absa));
        Assert.Contains("doesn't cover", await ProblemAsync(tooMuch));
        Assert.Contains("week from 1 to 52", await ProblemAsync(badWeek));
        Assert.Equal(50_000m, (await buyer.GetAsync<RwaMeDto>($"{Base}/me")).SimBalanceUsd);
        Assert.Empty((await buyer.GetAsync<RwaPortfolioDto>($"{Base}/portfolio")).Holdings);
    }

    [Fact]
    public async Task With_the_gate_off_nothing_can_be_bought()
    {
        var buyer = await VerifiedAsync("Rwa Gate Off");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
        var user = await db.Users.SingleAsync(u => u.Id == buyer.Id);
        var off = new RwaService(db, Options.Create(new RwaOptions()), TimeProvider.System, NullLogger<RwaService>.Instance);

        var refused = await Assert.ThrowsAsync<NdeipiChat.Api.Chat.ChatRejectedException>(() =>
            off.MintAsync(user, new RwaMintRequest(Villa, RwaIntervals.Week, 34, RwaPaymentMethods.Simulated, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal(RwaModes.Off, new RwaOptions().Mode);
        Assert.Contains("switched off", refused.Message);
        Assert.Equal(RwaModes.Off, (await off.MeAsync(user, CancellationToken.None)).Mode);
    }

    [Fact]
    public async Task Cancelling_inside_the_cooling_off_refunds_in_full_and_frees_the_week()
    {
        var buyer = await VerifiedAsync("Rwa Changed Mind");
        var stranger = await VerifiedAsync("Rwa Stranger");
        var holding = await MintAsync(buyer, Villa, RwaIntervals.Week, 18);

        using var notYours = await stranger.Http.PostAsJsonAsync($"{Base}/holdings/{holding.Id}/cancel", new { });
        var cancelled = await buyer.PostAsync<RwaHoldingDto>($"{Base}/holdings/{holding.Id}/cancel", new { });

        Assert.Equal(HttpStatusCode.NotFound, notYours.StatusCode);
        Assert.Equal(RwaHoldingStatus.Cancelled, cancelled.Status);
        Assert.NotNull(cancelled.CancelledAt);
        var portfolio = await buyer.GetAsync<RwaPortfolioDto>($"{Base}/portfolio");
        Assert.Equal((50_000m, 0m, 0m), (portfolio.SimBalanceUsd, portfolio.InvestedUsd, portfolio.InCoolingOffUsd));
        Assert.Equal(RwaHoldingStatus.Cancelled, Assert.Single(portfolio.Holdings).Status);
        // The week is back on sale.
        Assert.Equal(RwaHoldingStatus.CoolingOff, (await MintAsync(stranger, Villa, RwaIntervals.Week, 18)).Status);
    }

    [Fact]
    public async Task After_the_cooling_off_the_money_vests_into_the_vault_and_grows_with_the_oracles_NAV()
    {
        var buyer = await VerifiedAsync("Rwa Investor");
        var oracle = await VerifiedAsync("Rwa Oracle", [FinanceContract.AdminRole]);
        var holding = await MintAsync(buyer, Condo, RwaIntervals.Week, 28);
        var navBefore = (await buyer.GetAsync<RwaAssetDetailDto>($"{Base}/assets/{Condo}")).Asset;

        await EndCoolingOffAsync(holding.Id);
        using var tooLate = await buyer.Http.PostAsJsonAsync($"{Base}/holdings/{holding.Id}/cancel", new { });
        var vested = Assert.Single((await buyer.GetAsync<RwaPortfolioDto>($"{Base}/portfolio")).Holdings);

        Assert.Contains("cooling-off period has ended", await ProblemAsync(tooLate));
        Assert.Equal(RwaHoldingStatus.Vested, vested.Status);
        Assert.Equal(Math.Round(holding.PrincipalUsd / navBefore.AssetsPerShare, 6), vested.VaultShares);
        Assert.Contains(await app.DbAsync(db => db.RwaAuditEvents.Where(e => e.HoldingId == holding.Id).Select(e => e.Kind).ToListAsync()), k => k == "vest");

        // FR-2.4: the oracle posts a 5% higher NAV, and the share is worth 5% more (no cash paid out).
        var newNav = Math.Round(navBefore.CurrentNavUsd * 1.05m, 6);
        var asset = await oracle.PostAsync<RwaAssetDto>($"{Base}/admin/nav", new RwaNavUpdateRequest(Condo, newNav, "QS certificate GI-2026-Q4"));
        var after = (await buyer.GetAsync<RwaPortfolioDto>($"{Base}/portfolio"));

        Assert.Equal(newNav, asset.CurrentNavUsd);
        Assert.Equal(Math.Round(vested.VaultShares * RwaService.AssetsPerShare(asset.BaseNavUsd, newNav), 2), after.ValueUsd);
        Assert.True(after.ValueUsd > holding.PrincipalUsd);
        Assert.Equal(50_000m - holding.PrincipalUsd, after.SimBalanceUsd);
    }

    [Fact]
    public async Task Only_an_oracle_moves_a_NAV_by_a_bounded_step_with_a_stated_basis()
    {
        var investor = await VerifiedAsync("Rwa Not Oracle");
        var oracle = await VerifiedAsync("Rwa Bounded Oracle", [FinanceContract.AdminRole]);
        var nav = (await oracle.GetAsync<RwaAssetDetailDto>($"{Base}/assets/{Villa}")).Asset.CurrentNavUsd;

        using var notOracle = await investor.Http.PostAsJsonAsync($"{Base}/admin/nav", new RwaNavUpdateRequest(Villa, nav * 1.01m, "mine"), ContractJson.Options);
        using var tooFar = await oracle.Http.PostAsJsonAsync($"{Base}/admin/nav", new RwaNavUpdateRequest(Villa, nav * 1.5m, "pump"), ContractJson.Options);
        using var noBasis = await oracle.Http.PostAsJsonAsync($"{Base}/admin/nav", new RwaNavUpdateRequest(Villa, nav * 1.01m, " "), ContractJson.Options);

        Assert.Equal(HttpStatusCode.Forbidden, notOracle.StatusCode);
        Assert.Contains("at most 10%", await ProblemAsync(tooFar));
        Assert.Contains("based on", await ProblemAsync(noBasis));
        Assert.Equal(nav, (await oracle.GetAsync<RwaAssetDetailDto>($"{Base}/assets/{Villa}")).Asset.CurrentNavUsd);
        Assert.True((await oracle.GetAsync<RwaMeDto>($"{Base}/me")).IsOracle);
    }
}
