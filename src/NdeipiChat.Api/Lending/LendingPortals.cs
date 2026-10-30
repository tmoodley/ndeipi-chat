using Microsoft.EntityFrameworkCore;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Lending;

/// <summary>
/// What the Broker, Lender and Borrower portals add to the shared engine: submitting a deal for
/// underwriting, redemption windows, a developer's own repayments, in-app notices, and each portal's
/// home screen. These follow docs/lending-portals-requirements-DRAFT.md, drafted because the SRS's
/// portal tabs don't exist.
/// </summary>
public sealed partial class LendingService
{
    // ------------------------------------------------------------------ broker (BRK-FR-05 to BRK-FR-07)

    public Task<LendingDealDto?> SubmitAsync(User me, Guid id, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Broker, ct);
        if (deal.State != LendingStates.Draft)
            throw new ChatRejectedException("Only a draft can be submitted.");
        if (!await db.LendingDealRoles.AsNoTracking().AnyAsync(x => x.DealId == id && x.Role == LendingRoles.Developer, ct))
            throw new ChatRejectedException("Appoint the project developer first.");
        (deal.SubmittedAt, deal.ReturnReason) = (Now, null);
        Audit(id, me, "deal.submit", null, null, null);
    }, ct);

    public Task<LendingDealDto?> ReturnAsync(User me, Guid id, LendingReasonRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Underwriter, ct);
        if (deal.State != LendingStates.Draft || deal.SubmittedAt is null)
            throw new ChatRejectedException("Only a submitted draft can be returned.");
        var reason = RequireReason(r.Reason);
        (deal.SubmittedAt, deal.ReturnReason) = (null, reason);
        Audit(id, me, "deal.return", null, null, reason);
        await NotifyAsync(id, $"{deal.Name} was returned: {reason}", ct);
    }, ct);

    public async Task<BrokerPipelineDto> PipelineAsync(User me, CancellationToken ct)
    {
        var ids = await db.LendingDealRoles.AsNoTracking().Where(x => x.UserId == me.Id && x.Role == LendingRoles.Broker).Select(x => x.DealId).ToListAsync(ct);
        var deals = await db.LendingDeals.AsNoTracking().Where(d => ids.Contains(d.Id)).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        var roles = await db.LendingDealRoles.AsNoTracking().Where(x => ids.Contains(x.DealId)).ToListAsync(ct);
        var withDocuments = await db.LendingDocuments.AsNoTracking().Where(d => ids.Contains(d.DealId)).Select(d => d.DealId).Distinct().ToListAsync(ct);
        var draws = await db.LendingDraws.AsNoTracking().Where(d => ids.Contains(d.DealId) && d.DrawnAt != null).OrderBy(d => d.Number).ToListAsync(ct);
        var developers = roles.Where(x => x.Role == LendingRoles.Developer).Select(x => x.UserId).ToList();
        var kyb = await db.LendingProfiles.AsNoTracking().Where(p => developers.Contains(p.UserId) && p.KybApproved).Select(p => p.UserId).ToListAsync(ct);

        var rows = deals.Select(d =>
        {
            var on = roles.Where(x => x.DealId == d.Id).ToList();
            bool Named(string role) => on.Any(x => x.Role == role);
            // A fee is only this broker's if it was fixed to them at approval (SC-FEE-01).
            var fees = d.BrokerFeeUserId == me.Id ? draws.Where(x => x.DealId == d.Id).Select(x => x.BrokerFeeUsd ?? 0m).ToList() : [];
            return new BrokerDealDto(Summary(d, on.Where(x => x.UserId == me.Id).Select(x => x.Role).ToList(), 0m),
            [
                new("Data room started", withDocuments.Contains(d.Id)),
                new("Developer invited", Named(LendingRoles.Developer)),
                new("Developer passed KYB", on.Any(x => x.Role == LendingRoles.Developer && kyb.Contains(x.UserId))),
                new("Trustee, servicer and inspector named", LendingRoles.RequiredToOpen.All(Named)),
                new("Submitted to the underwriter", d.SubmittedAt is not null),
                new("Terms approved", d.State is not (LendingStates.Draft or LendingStates.Rejected)),
                new("Land charge registered", d.LandChargeRegistered),
                new("Both tranches full", d.SeniorFundedUsd >= d.SeniorTargetUsd && d.JuniorFundedUsd >= d.JuniorTargetUsd)
            ], d.SubmittedAt, d.ReturnReason, fees.Sum(), fees);
        }).ToList();

        var queue = !IsUnderwriter(me) ? [] : await db.LendingDeals.AsNoTracking().Where(d => d.State == LendingStates.Draft && d.SubmittedAt != null).OrderBy(d => d.SubmittedAt).ToListAsync(ct);
        return new BrokerPipelineDto(rows, queue.Select(d => Summary(d, [LendingRoles.Underwriter], 0m)).ToList(), rows.Sum(r => r.FeesEarnedUsd));
    }

    // ------------------------------------------------------------------ lender (LND-FR-01, LND-FR-05, LND-FR-07)

    public async Task<LenderPortfolioDto> PortfolioAsync(User me, CancellationToken ct)
    {
        var positions = await db.LendingPositions.AsNoTracking().Where(p => p.UserId == me.Id).ToListAsync(ct);
        var dealIds = positions.Select(p => p.DealId).ToList();
        var deals = await db.LendingDeals.AsNoTracking().Where(d => dealIds.Contains(d.Id) || d.State == LendingStates.Funding).ToListAsync(ct);
        var rows = positions.Select(p =>
        {
            var d = deals.First(x => x.Id == p.DealId);
            var senior = p.Tranche == LendingTranches.Senior;
            var price = senior ? LendingEngine.SharePrice(d.SeniorFundedUsd - d.SeniorRedeemedUsd, d.SeniorLossUsd) : LendingEngine.SharePrice(d.JuniorFundedUsd - d.JuniorRedeemedUsd, d.JuniorLossUsd);
            return new LenderPositionDto(d.Id, d.Name, d.State, p.Tranche, p.DepositedUsd, p.RedeemedUsd, p.InterestPaidUsd, p.PrincipalPaidUsd, p.LossUsd, price,
                Math.Max(p.DepositedUsd - p.RedeemedUsd - p.PrincipalPaidUsd - p.LossUsd, 0m));
        }).ToList();

        var pools = new List<LenderPoolDto>();
        foreach (var d in deals.Where(x => x.State == LendingStates.Funding).OrderByDescending(x => x.CreatedAt))
            pools.Add(new LenderPoolDto(Summary(d, [], positions.Where(p => p.DealId == d.Id).Sum(p => p.DepositedUsd)), d.SeniorAprBps, d.JuniorAprBps, await WhyNotLendAsync(me, d, ct)));
        return new LenderPortfolioDto(rows, pools, rows.Sum(r => r.DepositedUsd), rows.Sum(r => r.InterestPaidUsd), rows.Sum(r => r.PrincipalPaidUsd), rows.Sum(r => r.LossUsd), rows.Sum(r => r.ValueUsd));
    }

    /// <summary>SC-VLT-01: asks to take money out. Nothing moves until the window opens.</summary>
    public Task<LendingDealDto?> RequestRedemptionAsync(User me, Guid id, LendingRedeemRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        if (deal.State != LendingStates.Active)
            throw new ChatRejectedException("Redemptions are only open while the loan is active.");
        if (r.AmountUsd <= 0 || r.AmountUsd != Math.Round(r.AmountUsd, 2) || !LendingTranches.IsValid(r.Tranche))
            throw new ChatRejectedException("Choose a tranche and an amount.");
        var position = await db.LendingPositions.AsNoTracking().FirstOrDefaultAsync(p => p.DealId == id && p.UserId == me.Id && p.Tranche == r.Tranche, ct)
            ?? throw new ChatRejectedException("You have nothing in that tranche.");
        var pending = await db.LendingRedemptions.AsNoTracking().Where(x => x.DealId == id && x.UserId == me.Id && x.Tranche == r.Tranche && x.Status == LendingRedemptions.Requested && x.WindowClosesAt > Now).SumAsync(x => x.AmountUsd, ct);
        if (r.AmountUsd + pending > position.DepositedUsd - position.RedeemedUsd)
            throw new ChatRejectedException("That's more than you have in that tranche.");
        var opens = Now.AddDays(LendingRedemptions.NoticeDays);
        db.LendingRedemptions.Add(new LendingRedemption
        {
            Id = Guid.NewGuid(), DealId = id, UserId = me.Id, Tranche = r.Tranche, AmountUsd = r.AmountUsd, Status = LendingRedemptions.Requested, RequestedAt = Now,
            WindowOpensAt = opens, WindowClosesAt = opens.AddDays(LendingRedemptions.WindowDays)
        });
        Audit(id, me, "redemption.request", null, LendingContract.Money(r.AmountUsd), r.Tranche);
    }, ct);

    /// <summary>Pays a redemption in its window, only from money the drawdown plan doesn't need.</summary>
    public Task<LendingDealDto?> RedeemAsync(User me, Guid id, Guid redemptionId, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        var redemption = await db.LendingRedemptions.FirstOrDefaultAsync(x => x.Id == redemptionId && x.DealId == id && x.UserId == me.Id, ct)
            ?? throw new ChatRejectedException("That redemption isn't yours.");
        if (redemption.Status != LendingRedemptions.Requested)
            throw new ChatRejectedException("That redemption has been paid.");
        if (deal.State != LendingStates.Active)
            throw new ChatRejectedException("Redemptions are only open while the loan is active.");
        if (Now < redemption.WindowOpensAt)
            throw new ChatRejectedException($"The window opens {redemption.WindowOpensAt:d MMM yyyy}.");
        if (Now > redemption.WindowClosesAt)
            throw new ChatRejectedException("That window has closed. Make a new request.");

        var senior = redemption.Tranche == LendingTranches.Senior;
        var planned = await db.LendingDraws.AsNoTracking().Where(d => d.DealId == id).SumAsync(d => d.AmountUsd, ct);
        var (seniorPlan, juniorPlan) = LendingEngine.Allocate(planned, deal.SeniorFundedUsd, deal.JuniorFundedUsd);
        var free = senior ? deal.SeniorFundedUsd - deal.SeniorRedeemedUsd - seniorPlan : deal.JuniorFundedUsd - deal.JuniorRedeemedUsd - juniorPlan;
        if (redemption.AmountUsd > free)
            throw new ChatRejectedException($"Only {LendingContract.Money(Math.Max(free, 0m))} of this tranche isn't needed for drawdowns.");

        var position = await db.LendingPositions.FirstAsync(p => p.DealId == id && p.UserId == me.Id && p.Tranche == redemption.Tranche, ct);
        position.RedeemedUsd += redemption.AmountUsd;
        if (senior)
            deal.SeniorRedeemedUsd += redemption.AmountUsd;
        else
            deal.JuniorRedeemedUsd += redemption.AmountUsd;
        (redemption.Status, redemption.RedeemedAt) = (LendingRedemptions.Redeemed, Now);
        await CreditAsync(me.Id, redemption.AmountUsd, ct);
        Audit(id, me, "redemption.pay", null, LendingContract.Money(redemption.AmountUsd), redemption.Tranche);
    }, ct);

    // ------------------------------------------------------------------ borrower (BOR-FR-01, BOR-FR-05, BOR-FR-06)

    public async Task<BorrowerLoansDto> LoansAsync(User me, CancellationToken ct)
    {
        var ids = await db.LendingDealRoles.AsNoTracking().Where(x => x.UserId == me.Id && x.Role == LendingRoles.Developer).Select(x => x.DealId).ToListAsync(ct);
        var deals = await db.LendingDeals.AsNoTracking().Where(d => ids.Contains(d.Id)).OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        var loans = new List<BorrowerLoanDto>();
        foreach (var d in deals)
        {
            BringUpToDate(d);
            var next = await db.LendingDraws.AsNoTracking().Where(x => x.DealId == d.Id && x.DrawnAt == null).OrderBy(x => x.Number).FirstOrDefaultAsync(ct);
            var attested = next is not null && await db.LendingAttestations.AsNoTracking().AnyAsync(a => a.DealId == d.Id && a.Kind == LendingAttestationKinds.Milestone && a.DrawNumber == next.Number, ct);
            var drawn = d.SeniorOutUsd + d.JuniorOutUsd;
            var health = LendingEngine.HealthFactor(d.MaxLtvBps, drawn, d.ValuationUsd);
            loans.Add(new BorrowerLoanDto(Summary(d, [LendingRoles.Developer], 0m), drawn, DueNow(d), health, health < 1m, d.CollateralLocked,
                next is null ? null : new LendingDrawDto(next.Number, next.AmountUsd, next.Milestone, attested, null, null, null, null, await BlockersAsync(d, next.Number, next.AmountUsd, ct))));
        }
        return new BorrowerLoansDto(loans, await db.LendingProfiles.AsNoTracking().AnyAsync(p => p.UserId == me.Id && p.KybApproved, ct));
    }

    /// <summary>Interest and fees owed now, plus the next scheduled senior principal.</summary>
    static decimal DueNow(LendingDeal d) =>
        Math.Round(d.FeesDueUsd + d.SeniorDueUsd + d.JuniorDueUsd, 2) + Math.Min(d.ScheduledSeniorPrincipalUsd, d.SeniorOutUsd);

    public Task<LendingDealDto?> RepayAsync(User me, Guid id, LendingRepayRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Developer, ct);
        if (!LendingStates.IsOpen(deal.State))
            throw new ChatRejectedException("This loan isn't taking payments.");
        if (r.AmountUsd <= 0 || r.AmountUsd != Math.Round(r.AmountUsd, 2))
            throw new ChatRejectedException("Give an amount.");
        await rwa.EnsureAccountAsync(me.Id, ct);
        var paid = await db.RwaSimAccounts.Where(a => a.UserId == me.Id && a.BalanceUsd >= r.AmountUsd)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.BalanceUsd, a => a.BalanceUsd - r.AmountUsd), ct);
        if (paid == 0)
            throw new ChatRejectedException($"Your simulated balance doesn't cover {LendingContract.Money(r.AmountUsd)}.");
        await ApplyPaymentAsync(deal, me, $"BORROWER-{Guid.NewGuid():N}"[..21], r.AmountUsd, ct);
    }, ct);

    public Task<LendingDealDto?> RequestInspectionAsync(User me, Guid id, LendingInspectionRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Developer, ct);
        var draw = await db.LendingDraws.AsNoTracking().FirstOrDefaultAsync(d => d.DealId == id && d.Number == r.DrawNumber, ct)
            ?? throw new ChatRejectedException("That drawdown isn't in the plan.");
        var inspector = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == id && x.Role == LendingRoles.Inspector).Select(x => (Guid?)x.UserId).FirstOrDefaultAsync(ct)
            ?? throw new ChatRejectedException("No inspector is appointed.");
        Audit(id, me, "milestone.request", null, r.DrawNumber.ToString(), draw.Milestone);
        Notice(inspector, id, $"{deal.Name}: the developer asks you to inspect tranche {r.DrawNumber}, {draw.Milestone}.");
    }, ct);

    // ------------------------------------------------------------------ self-declaring (simulated only)

    /// <summary>
    /// Lets someone try lending without waiting for Compliance. The gate means this only works while
    /// simulated: a real offering must verify this (PLT-ID-04). It can't approve KYB or lift a freeze.
    /// </summary>
    public async Task<LendingMeDto> DeclareAsync(User me, LendingDeclareRequest r, CancellationToken ct)
    {
        Gate();
        if (!LendingInvestorClasses.IsValid(r.InvestorClass))
            throw new ChatRejectedException("Choose an investor class.");
        var jurisdiction = r.Jurisdiction?.Trim().ToUpperInvariant() ?? "";
        if (jurisdiction.Length != 2 || !jurisdiction.All(char.IsAsciiLetterUpper))
            throw new ChatRejectedException("Give a two-letter country code.");
        var profile = await db.LendingProfiles.FirstOrDefaultAsync(p => p.UserId == me.Id, ct);
        if (profile?.Frozen == true)
            throw new ChatRejectedException("Compliance has frozen your account.");
        var before = profile is null ? null : Describe(profile);
        if (profile is null)
            db.LendingProfiles.Add(profile = new LendingProfile { UserId = me.Id, InvestorClass = r.InvestorClass, Jurisdiction = jurisdiction });
        (profile.InvestorClass, profile.Jurisdiction, profile.Reason, profile.UpdatedAt) = (r.InvestorClass, jurisdiction, "Self-declared (simulated)", Now);
        Audit(null, me, "investor.declare", before, Describe(profile), "Self-declared (simulated)");
        await db.SaveChangesAsync(ct);
        return await MeAsync(me, ct);
    }

    // ------------------------------------------------------------------ notices (PLT-NTF-01, in-app)

    public async Task<IReadOnlyList<LendingNoticeDto>> NoticesAsync(User me, CancellationToken ct) =>
        (await db.LendingNotices.AsNoTracking().Where(n => n.UserId == me.Id).OrderByDescending(n => n.Id).Take(30).ToListAsync(ct))
        .Select(n => new LendingNoticeDto(n.Id, n.DealId, n.Text, n.At, n.ReadAt is not null)).ToList();

    public Task MarkNoticesReadAsync(User me, CancellationToken ct)
    {
        var now = Now;
        return db.LendingNotices.Where(n => n.UserId == me.Id && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
    }

    void Notice(Guid userId, Guid dealId, string text) =>
        db.LendingNotices.Add(new LendingNotice { UserId = userId, DealId = dealId, Text = Cut(text, 300)!, At = Now });

    /// <summary>Tells everyone on a deal, and its lenders. Saved with the change that caused it.</summary>
    async Task NotifyAsync(Guid dealId, string text, CancellationToken ct)
    {
        var roles = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == dealId).Select(x => x.UserId).ToListAsync(ct);
        var lenders = await db.LendingPositions.AsNoTracking().Where(p => p.DealId == dealId).Select(p => p.UserId).ToListAsync(ct);
        foreach (var user in roles.Concat(lenders).Distinct())
            Notice(user, dealId, text);
    }

    static LendingDealSummaryDto Summary(LendingDeal d, IReadOnlyList<string> myRoles, decimal deposited) =>
        new(d.Id, d.Code, d.Name, d.Location, d.State, d.OfferingBasis, d.SeniorTargetUsd, d.JuniorTargetUsd, d.SeniorFundedUsd, d.JuniorFundedUsd, myRoles, deposited);
}
