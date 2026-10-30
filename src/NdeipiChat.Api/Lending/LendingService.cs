using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NdeipiChat.Api.Chat;
using NdeipiChat.Api.Data;
using NdeipiChat.Api.Rwa;
using NdeipiChat.Contracts;

namespace NdeipiChat.Api.Lending;

/// <summary>The gate for Ndeipi Lending (configuration "Lending").</summary>
public sealed class LendingOptions
{
    public const string Section = "Lending";

    /// <summary>
    /// <see cref="LendingModes.Off"/> (the default) or <see cref="LendingModes.Simulated"/>. There is no
    /// live mode: real lending waits for counsel (REG-01 to REG-05) and a contract audit (SC-UPG-01).
    /// </summary>
    public string Mode { get; set; } = LendingModes.Off;

    public bool Simulated => string.Equals(Mode, LendingModes.Simulated, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The deal lifecycle and the simulated contract engine: terms and roles, the land-charge and KYB gates,
/// funding the tranches, locking collateral, drawing against attestations, running payments through the
/// waterfall, the 2-of-2 default, and write-off with junior-first losses. Every change holds the deal's
/// row lock, so two requests can't both spend the same balance, and writes the audit log (PLT-AUD-01).
/// </summary>
public sealed partial class LendingService(ChatDbContext db, RwaService rwa, IOptions<LendingOptions> options, TimeProvider clock)
{
    const string SeededCode = "GI-LOAN-01";

    DateTimeOffset Now => clock.GetUtcNow();

    static bool HasRole(User user, params string[] wanted) =>
        user.Roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Intersect(wanted, StringComparer.OrdinalIgnoreCase).Any();

    public static bool IsUnderwriter(User user) => HasRole(user, LendingContract.UnderwriterRole, FinanceContract.AdminRole);

    public static bool IsCompliance(User user) => HasRole(user, LendingContract.ComplianceRole, FinanceContract.AdminRole);

    public async Task<LendingMeDto> MeAsync(User me, CancellationToken ct)
    {
        var profile = await db.LendingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == me.Id, ct);
        var balance = options.Value.Simulated ? (await rwa.EnsureAccountAsync(me.Id, ct)).BalanceUsd : 0m;
        return new LendingMeDto(options.Value.Simulated ? LendingModes.Simulated : LendingModes.Off, await KycVerifiedAsync(me.Id, ct), IsUnderwriter(me), IsCompliance(me), balance,
            profile?.InvestorClass ?? LendingInvestorClasses.Unclassified, profile?.Jurisdiction, profile?.KybApproved == true, profile?.Frozen == true, LendingContract.SimulatedNotice);
    }

    // ------------------------------------------------------------------ deals

    /// <summary>The deals you're part of, and any whose pool has opened. A deal is private until then.</summary>
    public async Task<IReadOnlyList<LendingDealSummaryDto>> DealsAsync(User me, CancellationToken ct)
    {
        var platform = IsUnderwriter(me) || IsCompliance(me);
        var mine = await db.LendingDealRoles.AsNoTracking().Where(r => r.UserId == me.Id).ToListAsync(ct);
        var positions = await db.LendingPositions.AsNoTracking().Where(p => p.UserId == me.Id).ToListAsync(ct);
        var ids = mine.Select(r => r.DealId).Concat(positions.Select(p => p.DealId)).ToList();
        var deals = await db.LendingDeals.AsNoTracking()
            .Where(d => platform || ids.Contains(d.Id) || (d.State != LendingStates.Draft && d.State != LendingStates.Approved && d.State != LendingStates.Rejected))
            .OrderByDescending(d => d.CreatedAt).ToListAsync(ct);
        return deals.Select(d => new LendingDealSummaryDto(d.Id, d.Code, d.Name, d.Location, d.State, d.OfferingBasis, d.SeniorTargetUsd, d.JuniorTargetUsd, d.SeniorFundedUsd, d.JuniorFundedUsd,
            mine.Where(r => r.DealId == d.Id).Select(r => r.Role).ToList(), positions.Where(p => p.DealId == d.Id).Sum(p => p.DepositedUsd))).ToList();
    }

    public async Task<LendingDealDto> CreateAsync(User me, LendingTermsRequest r, CancellationToken ct)
    {
        Gate();
        if (!await KycVerifiedAsync(me.Id, ct))
            throw new ChatRejectedException("Verify your identity in your Wallet before originating a deal.");
        return await NewDealAsync(me, $"DEAL-{Guid.NewGuid():N}"[..13].ToUpperInvariant(), r, ct);
    }

    /// <summary>The Green Island reference deal, once. Its numbers are placeholders until the term sheet is in.</summary>
    public async Task<LendingDealDto> CreateReferenceAsync(User me, CancellationToken ct)
    {
        Gate();
        if (!IsUnderwriter(me))
            throw new ChatRejectedException("Only an underwriter can set up the reference deal.");
        if (await db.LendingDeals.AsNoTracking().FirstOrDefaultAsync(d => d.Code == SeededCode, ct) is { } existing)
            return (await DealAsync(me, existing.Id, ct))!;
        var villa = (await rwa.AssetsAsync(ct)).First(a => a.Code == "GI-VILLA-01");
        return await NewDealAsync(me, SeededCode, new LendingTermsRequest(
            "Green Island Phase 1", "Green Island, Negril, Jamaica", LendingOfferingBases.JamaicaFscExemption, 800_000m, 200_000m, 800, 1400, 100, 6000, 150, 100, 25_000m, 100_000m, null, null, villa.Id,
            [new(400_000m, "Land and permits"), new(350_000m, "Foundations and structure"), new(250_000m, "Fit-out and handover")]), ct);
    }

    async Task<LendingDealDto> NewDealAsync(User me, string code, LendingTermsRequest r, CancellationToken ct)
    {
        Validate(r);
        var deal = new LendingDeal
        {
            Id = Guid.NewGuid(), Code = code, Name = r.Name.Trim(), Location = r.Location.Trim(), State = LendingStates.Draft, OfferingBasis = r.OfferingBasis,
            CreatedBy = me.Id, CreatedAt = Now, UpdatedAt = Now
        };
        Apply(deal, r);
        db.LendingDeals.Add(deal);
        AddDraws(deal.Id, r);
        db.LendingDealRoles.Add(new LendingDealRole { DealId = deal.Id, Role = LendingRoles.Broker, UserId = me.Id });
        Audit(deal.Id, me, "deal.create", null, LendingStates.Draft, code);
        await db.SaveChangesAsync(ct);
        return (await DealAsync(me, deal.Id, ct))!;
    }

    /// <summary>SC-FAC-01: terms can change only until the pool opens. The broker proposes them in Draft; the underwriter can finalise them until then.</summary>
    public Task<LendingDealDto?> UpdateTermsAsync(User me, Guid id, LendingTermsRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        if (deal.State is not (LendingStates.Draft or LendingStates.Approved))
            throw new ChatRejectedException("The pool has opened, so these terms are fixed.");
        var roles = await RolesAsync(deal.Id, me, ct);
        if (!roles.Contains(LendingRoles.Underwriter) && !(deal.State == LendingStates.Draft && deal.SubmittedAt is null && roles.Contains(LendingRoles.Broker)))
            throw new ChatRejectedException("Only the broker (before submitting) or an underwriter can change the terms.");
        Validate(r);
        (deal.Name, deal.Location, deal.OfferingBasis) = (r.Name.Trim(), r.Location.Trim(), r.OfferingBasis);
        Apply(deal, r);
        db.LendingDraws.RemoveRange(await db.LendingDraws.Where(d => d.DealId == id).ToListAsync(ct));
        AddDraws(id, r);
        Audit(id, me, "deal.terms", null, null, "Terms changed");
    }, ct);

    public Task<LendingDealDto?> AppointAsync(User me, Guid id, LendingAppointRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        if (!LendingRoles.Appointed.Contains(r.Role))
            throw new ChatRejectedException("That isn't a role on a deal.");
        if (deal.State is not (LendingStates.Draft or LendingStates.Approved))
            throw new ChatRejectedException("Roles are fixed once the pool opens.");
        var roles = await RolesAsync(id, me, ct);
        if (!roles.Contains(LendingRoles.Broker) && !roles.Contains(LendingRoles.Underwriter))
            throw new ChatRejectedException("Only the broker or an underwriter can appoint roles.");
        var user = await UserByEmailAsync(r.Email, ct);

        // The Servicer and Trustee must be two people for 2-of-2 to mean anything (SC-DEF-01), and the
        // people who check the developer's work must not be the developer.
        var held = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == id && x.UserId == user.Id && x.Role != r.Role).Select(x => x.Role).ToListAsync(ct);
        if ((r.Role == LendingRoles.Servicer && held.Contains(LendingRoles.Trustee)) || (r.Role == LendingRoles.Trustee && held.Contains(LendingRoles.Servicer)))
            throw new ChatRejectedException("The servicer and the trustee must be different people.");
        string[] independent = [LendingRoles.Trustee, LendingRoles.Servicer, LendingRoles.Inspector, LendingRoles.Valuer];
        if ((r.Role == LendingRoles.Developer && held.Intersect(independent).Any()) || (independent.Contains(r.Role) && held.Contains(LendingRoles.Developer)))
            throw new ChatRejectedException("The developer can't also hold a role that checks their work.");

        var existing = await db.LendingDealRoles.FirstOrDefaultAsync(x => x.DealId == id && x.Role == r.Role, ct);
        Audit(id, me, "deal.appoint", existing?.UserId.ToString(), user.Id.ToString(), r.Role);
        if (existing is not null)
            db.LendingDealRoles.Remove(existing);
        await db.SaveChangesAsync(ct);
        db.LendingDealRoles.Add(new LendingDealRole { DealId = id, Role = r.Role, UserId = user.Id });
    }, ct);

    // ------------------------------------------------------------------ lifecycle

    public Task<LendingDealDto?> TransitionAsync(User me, Guid id, LendingTransitionRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        if (LendingStates.MoveRole(deal.State, r.To) is not { } role)
            throw new ChatRejectedException($"A {LendingStates.Label(deal.State)} deal can't go straight to {LendingStates.Label(r.To)}.");
        await RequireAsync(deal.Id, me, role, ct);
        var reason = r.Reason?.Trim();
        if (r.To is LendingStates.Rejected or LendingStates.Grace && string.IsNullOrEmpty(reason))
            throw new ChatRejectedException("Give a reason.");

        var roles = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == id).ToListAsync(ct);
        switch (r.To)
        {
            case LendingStates.Approved:
                if (deal.SubmittedAt is null)
                    throw new ChatRejectedException("The broker has to submit the deal first.");
                // Lifecycle step 2: the developer has passed KYB before terms are final.
                if (roles.FirstOrDefault(x => x.Role == LendingRoles.Developer) is not { } developer)
                    throw new ChatRejectedException("Appoint the project developer first.");
                if (!await db.LendingProfiles.AnyAsync(p => p.UserId == developer.UserId && p.KybApproved, ct))
                    throw new ChatRejectedException("Compliance hasn't approved the developer's KYB yet.");
                // SC-FEE-01: the fee wallet is fixed from here.
                deal.BrokerFeeUserId = roles.First(x => x.Role == LendingRoles.Broker).UserId;
                break;
            case LendingStates.Funding:
                var missing = LendingRoles.RequiredToOpen.Where(w => roles.All(x => x.Role != w)).Select(LendingRoles.Label).ToList();
                if (missing.Count > 0)
                    throw new ChatRejectedException($"Appoint these before the pool opens: {string.Join(", ", missing)}.");
                if (!deal.LandChargeRegistered)
                    throw new ChatRejectedException("No pool opens until the trustee attests the land charge is registered (REG-05).");
                break;
            case LendingStates.Active:
                if (deal.SeniorFundedUsd < deal.SeniorTargetUsd || deal.JuniorFundedUsd < deal.JuniorTargetUsd)
                    throw new ChatRejectedException("Both tranches must fill to target first.");
                deal.AccruedAt = Now;
                break;
            case LendingStates.Recovery:
                deal.CollateralLocked = true;
                break;
        }
        if (deal.State == LendingStates.Grace && r.To == LendingStates.Active)
            db.LendingDefaultVotes.RemoveRange(await db.LendingDefaultVotes.Where(v => v.DealId == id).ToListAsync(ct));
        Audit(id, me, "deal.state", deal.State, r.To, reason);
        deal.State = r.To;
        await NotifyAsync(id, $"{deal.Name} is now {LendingStates.Label(r.To)}.", ct);
    }, ct);

    /// <summary>SC-DEF-01: one half of markDefault. Once the Servicer and the Trustee have both signed, draws and collateral freeze and inflows go to the waterfall's default order.</summary>
    public Task<LendingDealDto?> VoteDefaultAsync(User me, Guid id, LendingReasonRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        if (deal.State != LendingStates.Grace)
            throw new ChatRejectedException("A loan must be in grace before it can be marked in default.");
        var reason = RequireReason(r.Reason);
        var mine = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == id && x.UserId == me.Id && (x.Role == LendingRoles.Servicer || x.Role == LendingRoles.Trustee)).Select(x => x.Role).FirstOrDefaultAsync(ct)
            ?? throw new ChatRejectedException("Only the servicer and the security trustee can mark a default.");
        var votes = await db.LendingDefaultVotes.Where(v => v.DealId == id).ToListAsync(ct);
        if (votes.All(v => v.Role != mine))
        {
            db.LendingDefaultVotes.Add(new LendingDefaultVote { DealId = id, Role = mine, UserId = me.Id, Reason = reason, At = Now });
            Audit(id, me, "default.sign", null, mine, reason);
        }
        if (votes.Any(v => v.Role != mine))
        {
            Audit(id, me, "deal.state", deal.State, LendingStates.Default, "2-of-2 signed");
            deal.State = LendingStates.Default;
            deal.CollateralFrozen = true;
            await NotifyAsync(id, $"{deal.Name} is in default. Draws and collateral are frozen.", ct);
        }
    }, ct);

    /// <summary>The trustee closes a recovery with nothing more to come: the reserve goes to senior, and what's left outstanding is lost, junior first (SC-VLT-02).</summary>
    public Task<LendingDealDto?> WriteOffAsync(User me, Guid id, LendingReasonRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Trustee, ct);
        if (deal.State != LendingStates.Recovery)
            throw new ChatRejectedException("Only a loan in recovery can be written off.");
        var reason = RequireReason(r.Reason);

        var fromReserve = Math.Min(deal.ReserveUsd, deal.SeniorOutUsd);
        deal.ReserveUsd -= fromReserve;
        deal.SeniorOutUsd -= fromReserve;
        deal.SeniorPrincipalPaidUsd += fromReserve;
        await DistributeAsync(deal, LendingTranches.Senior, 0m, fromReserve, ct);

        var (junior, senior) = LendingEngine.ApplyLoss(deal.SeniorOutUsd + deal.JuniorOutUsd, deal.JuniorOutUsd, deal.SeniorOutUsd);
        deal.JuniorLossUsd += junior;
        deal.SeniorLossUsd += senior;
        await ShareLossAsync(deal.Id, LendingTranches.Junior, junior, deal.JuniorFundedUsd - deal.JuniorRedeemedUsd, ct);
        await ShareLossAsync(deal.Id, LendingTranches.Senior, senior, deal.SeniorFundedUsd - deal.SeniorRedeemedUsd, ct);
        (deal.JuniorOutUsd, deal.SeniorOutUsd, deal.SeniorDueUsd, deal.JuniorDueUsd, deal.FeesDueUsd) = (0m, 0m, 0m, 0m, 0m);
        await ReturnUndrawnAsync(deal, ct);
        Audit(id, me, "deal.state", deal.State, LendingStates.WrittenOff, $"{reason} (junior lost {LendingContract.Money(junior)}, senior lost {LendingContract.Money(senior)})");
        deal.State = LendingStates.WrittenOff;
        await NotifyAsync(id, $"{deal.Name} was written off.", ct);
    }, ct);

    // ------------------------------------------------------------------ funding

    public Task<LendingDealDto?> DepositAsync(User me, Guid id, LendingDepositRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        if (r.RequestId == Guid.Empty || r.AmountUsd <= 0 || r.AmountUsd != Math.Round(r.AmountUsd, 2) || !LendingTranches.IsValid(r.Tranche))
            throw new ChatRejectedException("Choose a tranche and an amount.");
        if (await db.LendingDeposits.AsNoTracking().AnyAsync(d => d.UserId == me.Id && d.RequestId == r.RequestId, ct))
            return;
        if (await WhyNotLendAsync(me, deal, ct) is { } why)
            throw new ChatRejectedException(why);

        var senior = r.Tranche == LendingTranches.Senior;
        var room = senior ? deal.SeniorTargetUsd - deal.SeniorFundedUsd : deal.JuniorTargetUsd - deal.JuniorFundedUsd;
        if (r.AmountUsd > room)
            throw new ChatRejectedException($"Only {LendingContract.Money(room)} of that tranche is left.");
        var position = await db.LendingPositions.FirstOrDefaultAsync(p => p.DealId == id && p.UserId == me.Id && p.Tranche == r.Tranche, ct);
        var holding = await db.LendingPositions.Where(p => p.DealId == id && p.UserId == me.Id).SumAsync(p => p.DepositedUsd, ct);
        if (deal.MaxHoldingUsd is { } limit && holding + r.AmountUsd > limit)
            throw new ChatRejectedException($"This deal limits each lender to {LendingContract.Money(limit)}.");

        await rwa.EnsureAccountAsync(me.Id, ct);
        var paid = await db.RwaSimAccounts.Where(a => a.UserId == me.Id && a.BalanceUsd >= r.AmountUsd)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.BalanceUsd, a => a.BalanceUsd - r.AmountUsd), ct);
        if (paid == 0)
            throw new ChatRejectedException($"Your simulated balance doesn't cover {LendingContract.Money(r.AmountUsd)}.");

        if (position is null)
            db.LendingPositions.Add(position = new LendingPosition { DealId = id, UserId = me.Id, Tranche = r.Tranche });
        position.DepositedUsd += r.AmountUsd;
        if (senior)
            deal.SeniorFundedUsd += r.AmountUsd;
        else
            deal.JuniorFundedUsd += r.AmountUsd;
        db.LendingDeposits.Add(new LendingDeposit { UserId = me.Id, RequestId = r.RequestId, DealId = id, Tranche = r.Tranche, AmountUsd = r.AmountUsd, At = Now });
    }, ct);

    // ------------------------------------------------------------------ documents and attestations

    /// <summary>PLT-DOC-01: records a document's hash under the next version of its name. The file stays with whoever hashed it.</summary>
    public Task<LendingDealDto?> AddDocumentAsync(User me, Guid id, LendingDocumentRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        var roles = await RolesAsync(id, me, ct);
        if (roles.Count == 0)
            throw new ChatRejectedException("Only the people on this deal can add to its data room.");
        var name = r.Name?.Trim() ?? "";
        if (name.Length is 0 or > LendingContract.MaxNameLength || !LendingDocumentKinds.IsValid(r.Kind))
            throw new ChatRejectedException("Give the document a name and a kind.");
        if (!LendingContract.IsSha256(r.Sha256))
            throw new ChatRejectedException("That isn't a SHA-256 hash.");
        var version = await db.LendingDocuments.Where(d => d.DealId == id && d.Name == name).MaxAsync(d => (int?)d.Version, ct) ?? 0;
        db.LendingDocuments.Add(new LendingDocument { Id = Guid.NewGuid(), DealId = id, Name = name, Kind = r.Kind, Version = version + 1, Sha256 = r.Sha256, UploadedBy = me.Id, At = Now });
        Audit(id, me, "document.anchor", null, r.Sha256, $"{name} v{version + 1}");
    }, ct);

    public Task<LendingDealDto?> AttestAsync(User me, Guid id, LendingAttestationRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        if (LendingAttestationKinds.AttestorRole(r.Kind) is not { } role)
            throw new ChatRejectedException("That isn't a kind of attestation.");
        var mine = await db.LendingDealRoles.AsNoTracking().AnyAsync(x => x.DealId == id && x.UserId == me.Id && x.Role == role, ct);
        if (!mine)
            throw new ChatRejectedException($"Only the deal's {LendingRoles.Label(role).ToLowerInvariant()} can sign this.");
        if (!LendingContract.IsSha256(r.ReportSha256))
            throw new ChatRejectedException("An attestation must carry its report's SHA-256 hash.");
        switch (r.Kind)
        {
            case LendingAttestationKinds.Valuation:
                if (r.ValueUsd is not > 0)
                    throw new ChatRejectedException("Give the appraised value.");
                (deal.ValuationUsd, deal.ValuedAt) = (r.ValueUsd, Now);
                break;
            case LendingAttestationKinds.Milestone:
                if (r.DrawNumber is not { } number || !await db.LendingDraws.AnyAsync(d => d.DealId == id && d.Number == number, ct))
                    throw new ChatRejectedException("Say which drawdown this milestone releases.");
                break;
            case LendingAttestationKinds.LandCharge:
                deal.LandChargeRegistered = true;
                break;
        }
        db.LendingAttestations.Add(new LendingAttestation { Id = Guid.NewGuid(), DealId = id, Kind = r.Kind, AttestorId = me.Id, ReportSha256 = r.ReportSha256, ValueUsd = r.ValueUsd, DrawNumber = r.DrawNumber, At = Now });
        Audit(id, me, "attest." + r.Kind, null, r.ReportSha256, null);
        if (r.Kind == LendingAttestationKinds.Milestone)
            await NotifyAsync(id, $"{deal.Name}: the milestone for tranche {r.DrawNumber} is attested.", ct);
        if (r.Kind == LendingAttestationKinds.Valuation && LendingEngine.HealthFactor(deal.MaxLtvBps, deal.SeniorOutUsd + deal.JuniorOutUsd, deal.ValuationUsd) < 1m)
            await NotifyAsync(id, $"{deal.Name}: margin event. The new valuation puts health below 1.", ct);
    }, ct);

    // ------------------------------------------------------------------ the loan

    public Task<LendingDealDto?> LockCollateralAsync(User me, Guid id, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Developer, ct);
        if (deal.State != LendingStates.Active)
            throw new ChatRejectedException("Collateral is locked once the loan is active.");
        deal.CollateralLocked = true;
        Audit(id, me, "collateral.lock", "unlocked", "locked", null);
    }, ct);

    /// <summary>SC-LN-01, SC-FEE-01: the next planned draw, if its checks pass, with fees taken at source.</summary>
    public Task<LendingDealDto?> DrawAsync(User me, Guid id, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Developer, ct);
        var draw = await db.LendingDraws.Where(d => d.DealId == id && d.DrawnAt == null).OrderBy(d => d.Number).FirstOrDefaultAsync(ct)
            ?? throw new ChatRejectedException("Every planned drawdown has been made.");
        var blockers = await BlockersAsync(deal, draw.Number, draw.AmountUsd, ct);
        if (blockers.Count > 0)
            throw new ChatRejectedException(blockers[0]);

        var (senior, junior) = LendingEngine.Allocate(draw.AmountUsd, deal.SeniorFundedUsd, deal.JuniorFundedUsd);
        deal.SeniorOutUsd += senior;
        deal.JuniorOutUsd += junior;
        var (broker, platform, net) = LendingEngine.SplitFees(draw.AmountUsd, deal.BrokerFeeBps, deal.PlatformFeeBps);
        (draw.DrawnAt, draw.BrokerFeeUsd, draw.PlatformFeeUsd, draw.NetUsd) = (Now, broker, platform, net);
        await CreditAsync(me.Id, net, ct);
        if (deal.BrokerFeeUserId is { } brokerId)
            await CreditAsync(brokerId, broker, ct);
        Audit(id, me, "loan.draw", null, draw.Number.ToString(), $"{LendingContract.Money(draw.AmountUsd)}: {LendingContract.Money(net)} to the developer, {LendingContract.Money(broker)} broker fee, {LendingContract.Money(platform)} platform fee");
        await NotifyAsync(id, $"{deal.Name}: tranche {draw.Number} was drawn.", ct);
    }, ct);

    /// <summary>PLT-PAY-02: the servicer pushes cash that has reached the collection account through the waterfall, once per reference.</summary>
    public Task<LendingDealDto?> PayAsync(User me, Guid id, LendingPaymentRequest r, CancellationToken ct) => ChangeAsync(me, id, async deal =>
    {
        await RequireAsync(id, me, LendingRoles.Servicer, ct);
        if (!LendingStates.IsOpen(deal.State))
            throw new ChatRejectedException("This loan isn't taking payments.");
        var reference = r.Reference?.Trim() ?? "";
        if (reference.Length is 0 or > 64)
            throw new ChatRejectedException("Give the transfer's reference.");
        if (r.AmountUsd <= 0 || r.AmountUsd != Math.Round(r.AmountUsd, 2))
            throw new ChatRejectedException("Give the amount received.");
        if (await db.LendingPayments.AsNoTracking().AnyAsync(p => p.DealId == id && p.Reference == reference, ct))
            throw new ChatRejectedException("That reference has already been recorded.");
        await ApplyPaymentAsync(deal, me, reference, r.AmountUsd, ct);
    }, ct);

    /// <summary>Runs cash through the waterfall and pays everyone it reaches: servicer payments and repayments by the developer alike.</summary>
    async Task ApplyPaymentAsync(LendingDeal deal, User me, string reference, decimal amount, CancellationToken ct)
    {
        var id = deal.Id;

        var inDefault = deal.State is LendingStates.Default or LendingStates.Recovery;
        var paid = LendingEngine.RunWaterfall(new WaterfallInput(amount, Math.Round(deal.FeesDueUsd, 2), Math.Round(deal.SeniorDueUsd, 2), deal.ScheduledSeniorPrincipalUsd, deal.SeniorOutUsd,
            Math.Round(deal.JuniorDueUsd, 2), deal.JuniorOutUsd, deal.ReserveUsd, deal.ReserveTargetUsd, inDefault));

        deal.FeesDueUsd = Math.Max(deal.FeesDueUsd - paid.Fees, 0m);
        deal.SeniorDueUsd = Math.Max(deal.SeniorDueUsd - paid.SeniorInterest, 0m);
        deal.JuniorDueUsd = Math.Max(deal.JuniorDueUsd - paid.JuniorInterest, 0m);
        var seniorPrincipal = paid.SeniorPrincipal + paid.SeniorPrincipalSweep;
        deal.SeniorOutUsd -= seniorPrincipal;
        deal.JuniorOutUsd -= paid.JuniorPrincipal;
        deal.SeniorInterestPaidUsd += paid.SeniorInterest;
        deal.JuniorInterestPaidUsd += paid.JuniorInterest;
        deal.SeniorPrincipalPaidUsd += seniorPrincipal;
        deal.JuniorPrincipalPaidUsd += paid.JuniorPrincipal;
        deal.ReserveUsd += paid.Reserve;
        await DistributeAsync(deal, LendingTranches.Senior, paid.SeniorInterest, seniorPrincipal, ct);
        await DistributeAsync(deal, LendingTranches.Junior, paid.JuniorInterest, paid.JuniorPrincipal, ct);
        if (paid.Residual > 0 && await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == id && x.Role == LendingRoles.Developer).Select(x => (Guid?)x.UserId).FirstOrDefaultAsync(ct) is { } developer)
            await CreditAsync(developer, paid.Residual, ct);

        db.LendingPayments.Add(new LendingPayment
        {
            Id = Guid.NewGuid(), DealId = id, Reference = reference, AmountUsd = amount, Fees = paid.Fees, SeniorInterest = paid.SeniorInterest, SeniorPrincipal = paid.SeniorPrincipal,
            JuniorInterest = paid.JuniorInterest, SeniorPrincipalSweep = paid.SeniorPrincipalSweep, JuniorPrincipal = paid.JuniorPrincipal, Reserve = paid.Reserve, Residual = paid.Residual, RecordedBy = me.Id, At = Now
        });
        Audit(id, me, "loan.payment", null, LendingContract.Money(amount), reference);
        await NotifyAsync(id, $"{deal.Name}: {LendingContract.Money(amount)} went through the waterfall.", ct);

        // Repaid, and the collateral comes back, once nothing is owed and nothing more is to be drawn.
        var cleared = deal.SeniorOutUsd == 0 && deal.JuniorOutUsd == 0 && Math.Round(deal.SeniorDueUsd, 2) == 0 && Math.Round(deal.JuniorDueUsd, 2) == 0 && Math.Round(deal.FeesDueUsd, 2) == 0;
        if (cleared && (inDefault || !await db.LendingDraws.AnyAsync(d => d.DealId == id && d.DrawnAt == null, ct)))
        {
            await ReturnUndrawnAsync(deal, ct);
            // The reserve has done its job; what it held is the borrower's again.
            if (await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == id && x.Role == LendingRoles.Developer).Select(x => (Guid?)x.UserId).FirstOrDefaultAsync(ct) is { } borrower)
                await CreditAsync(borrower, deal.ReserveUsd, ct);
            deal.ReserveUsd = 0m;
            (deal.CollateralLocked, deal.CollateralFrozen) = (false, false);
            Audit(id, me, "deal.state", deal.State, LendingStates.Repaid, "Paid in full");
            deal.State = LendingStates.Repaid;
            await NotifyAsync(id, $"{deal.Name} is repaid in full.", ct);
        }
    }

    // ------------------------------------------------------------------ compliance

    /// <summary>PLT-ID-04, PLT-CMP-02: classifies someone, approves their KYB, or freezes them, with a logged reason.</summary>
    public async Task SetProfileAsync(User me, LendingProfileRequest r, CancellationToken ct)
    {
        Gate();
        if (!IsCompliance(me))
            throw new ChatRejectedException("Only Compliance can do this.");
        if (!LendingInvestorClasses.IsValid(r.InvestorClass))
            throw new ChatRejectedException("Choose an investor class.");
        var jurisdiction = r.Jurisdiction?.Trim().ToUpperInvariant() ?? "";
        if (jurisdiction.Length != 2)
            throw new ChatRejectedException("Give a two-letter country code.");
        var reason = RequireReason(r.Reason);
        var user = await UserByEmailAsync(r.Email, ct);
        var profile = await db.LendingProfiles.FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        var before = profile is null ? null : Describe(profile);
        if (profile is null)
            db.LendingProfiles.Add(profile = new LendingProfile { UserId = user.Id, InvestorClass = r.InvestorClass, Jurisdiction = jurisdiction });
        (profile.InvestorClass, profile.Jurisdiction, profile.KybApproved, profile.Frozen, profile.Reason, profile.UpdatedAt) = (r.InvestorClass, jurisdiction, r.KybApproved, r.Frozen, reason, Now);
        Audit(null, me, "compliance.profile", before, $"{user.Id:N} {Describe(profile)}", reason);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Gives someone more test money, so a simulated deal can fill. Simulated mode only, because the gate above requires it.</summary>
    public async Task TopUpAsync(User me, LendingTopUpRequest r, CancellationToken ct)
    {
        Gate();
        if (!IsCompliance(me))
            throw new ChatRejectedException("Only Compliance can do this.");
        if (r.AmountUsd is <= 0 or > 10_000_000m)
            throw new ChatRejectedException("Give an amount up to $10,000,000.");
        var user = await UserByEmailAsync(r.Email, ct);
        await CreditAsync(user.Id, r.AmountUsd, ct);
        Audit(null, me, "sim.top_up", null, $"{user.Id:N} {LendingContract.Money(r.AmountUsd)}", "Simulated test balance");
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ reads

    public async Task<LendingDealDto?> DealAsync(User me, Guid id, CancellationToken ct)
    {
        var deal = await db.LendingDeals.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (deal is null)
            return null;
        var roles = await RolesAsync(id, me, ct);
        var positions = await db.LendingPositions.AsNoTracking().Where(p => p.DealId == id).ToListAsync(ct);
        var mine = positions.Where(p => p.UserId == me.Id).ToList();
        if (roles.Count == 0 && mine.Count == 0 && deal.State is LendingStates.Draft or LendingStates.Approved or LendingStates.Rejected)
            return null;

        BringUpToDate(deal);
        var appointed = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == id).ToListAsync(ct);
        var documents = await db.LendingDocuments.AsNoTracking().Where(d => d.DealId == id).OrderByDescending(d => d.At).ThenByDescending(d => d.Version).ToListAsync(ct);
        var attestations = await db.LendingAttestations.AsNoTracking().Where(a => a.DealId == id).OrderByDescending(a => a.At).ToListAsync(ct);
        var payments = await db.LendingPayments.AsNoTracking().Where(p => p.DealId == id).OrderByDescending(p => p.At).ToListAsync(ct);
        var votes = await db.LendingDefaultVotes.AsNoTracking().Where(v => v.DealId == id).ToListAsync(ct);
        var draws = await db.LendingDraws.AsNoTracking().Where(d => d.DealId == id).OrderBy(d => d.Number).ToListAsync(ct);
        var names = await NamesAsync(appointed.Select(a => a.UserId).Concat(documents.Select(d => d.UploadedBy)).Concat(attestations.Select(a => a.AttestorId))
            .Concat(payments.Select(p => p.RecordedBy)).Concat(votes.Select(v => v.UserId)), ct);
        string Name(Guid user) => names.GetValueOrDefault(user, "Someone");

        var drawn = deal.SeniorOutUsd + deal.JuniorOutUsd;
        var next = draws.FirstOrDefault(d => d.DrawnAt is null);
        var drawDtos = new List<LendingDrawDto>();
        foreach (var d in draws)
            drawDtos.Add(new LendingDrawDto(d.Number, d.AmountUsd, d.Milestone, attestations.Any(a => a.Kind == LendingAttestationKinds.Milestone && a.DrawNumber == d.Number), d.DrawnAt, d.BrokerFeeUsd, d.PlatformFeeUsd, d.NetUsd,
                d == next ? await BlockersAsync(deal, d.Number, d.AmountUsd, ct) : []));
        var health = LendingEngine.HealthFactor(deal.MaxLtvBps, drawn, deal.ValuationUsd);

        var terms = new LendingTermsRequest(deal.Name, deal.Location, deal.OfferingBasis, deal.SeniorTargetUsd, deal.JuniorTargetUsd, deal.SeniorAprBps, deal.JuniorAprBps, deal.FeesAprBps, deal.MaxLtvBps,
            deal.BrokerFeeBps, deal.PlatformFeeBps, deal.ReserveTargetUsd, deal.ScheduledSeniorPrincipalUsd, deal.MaxHoldingUsd, deal.BlockedJurisdictions, deal.RwaAssetId, draws.Select(d => new LendingDrawPlan(d.AmountUsd, d.Milestone)).ToList());
        return new LendingDealDto(deal.Id, deal.Code, deal.Name, deal.Location, deal.State, deal.OfferingBasis, terms, roles,
            LendingStates.MovesFrom(deal.State).Where(to => roles.Contains(LendingStates.MoveRole(deal.State, to)!)).ToList(),
            await WhyNotLendAsync(me, deal, ct),
            appointed.Select(a => new LendingParticipantDto(a.Role, a.UserId, Name(a.UserId))).ToList(),
            [
                new(LendingTranches.Senior, deal.SeniorTargetUsd, deal.SeniorFundedUsd, deal.SeniorAprBps, deal.SeniorOutUsd, Math.Round(deal.SeniorDueUsd, 2), deal.SeniorInterestPaidUsd, deal.SeniorPrincipalPaidUsd, deal.SeniorLossUsd, LendingEngine.SharePrice(deal.SeniorFundedUsd - deal.SeniorRedeemedUsd, deal.SeniorLossUsd)),
                new(LendingTranches.Junior, deal.JuniorTargetUsd, deal.JuniorFundedUsd, deal.JuniorAprBps, deal.JuniorOutUsd, Math.Round(deal.JuniorDueUsd, 2), deal.JuniorInterestPaidUsd, deal.JuniorPrincipalPaidUsd, deal.JuniorLossUsd, LendingEngine.SharePrice(deal.JuniorFundedUsd - deal.JuniorRedeemedUsd, deal.JuniorLossUsd))
            ],
            mine.Select(p => new LendingPositionDto(p.Tranche, p.DepositedUsd, p.InterestPaidUsd, p.PrincipalPaidUsd, p.LossUsd, p.RedeemedUsd)).ToList(),
            drawDtos,
            documents.Select(d => new LendingDocumentDto(d.Id, d.Name, d.Kind, d.Version, d.Sha256, Name(d.UploadedBy), d.At)).ToList(),
            attestations.Select(a => new LendingAttestationDto(a.Id, a.Kind, Name(a.AttestorId), a.ReportSha256, a.ValueUsd, a.DrawNumber, a.At)).ToList(),
            payments.Select(p => new LendingPaymentDto(p.Id, p.Reference, p.AmountUsd, new WaterfallResult(p.Fees, p.SeniorInterest, p.SeniorPrincipal, p.JuniorInterest, p.SeniorPrincipalSweep, p.JuniorPrincipal, p.Reserve, p.Residual), Name(p.RecordedBy), p.At)).ToList(),
            votes.Select(v => new LendingDefaultVoteDto(v.Role, Name(v.UserId), v.Reason, v.At)).ToList(),
            drawn, Math.Round(deal.FeesDueUsd, 2), deal.ReserveUsd, deal.ValuationUsd, deal.ValuedAt, deal.ValuationUsd is not null && LendingEngine.ValuationStale(deal.ValuedAt, Now),
            LendingEngine.Ltv(drawn, deal.ValuationUsd), health, health < 1m, deal.CollateralLocked, deal.CollateralFrozen, deal.LandChargeRegistered, deal.SubmittedAt, deal.ReturnReason,
            (await db.LendingRedemptions.AsNoTracking().Where(x => x.DealId == id && x.UserId == me.Id).OrderByDescending(x => x.RequestedAt).ToListAsync(ct))
                .Select(x => new LendingRedemptionDto(x.Id, x.Tranche, x.AmountUsd, x.Status, x.WindowOpensAt, x.WindowClosesAt, x.RedeemedAt)).ToList());
    }

    /// <summary>The audit trail: for the people on the deal, its lenders, and the platform.</summary>
    public async Task<IReadOnlyList<LendingAuditDto>?> AuditAsync(User me, Guid id, CancellationToken ct)
    {
        var allowed = (await RolesAsync(id, me, ct)).Count > 0 || await db.LendingPositions.AsNoTracking().AnyAsync(p => p.DealId == id && p.UserId == me.Id, ct);
        if (!allowed || !await db.LendingDeals.AsNoTracking().AnyAsync(d => d.Id == id, ct))
            return null;
        var events = await db.LendingAuditEvents.AsNoTracking().Where(e => e.DealId == id).OrderByDescending(e => e.Id).Take(200).ToListAsync(ct);
        var names = await NamesAsync(events.Select(e => e.ActorId), ct);
        return events.Select(e => new LendingAuditDto(e.Id, e.At, names.GetValueOrDefault(e.ActorId, "Someone"), e.Action, e.Before, e.After, e.Reason)).ToList();
    }

    // ------------------------------------------------------------------ helpers

    void Gate()
    {
        if (!options.Value.Simulated)
            throw new ChatRejectedException("Ndeipi Lending is switched off.");
    }

    /// <summary>Runs one change to a deal under its row lock, with interest brought up to date first.</summary>
    async Task<LendingDealDto?> ChangeAsync(User me, Guid id, Func<LendingDeal, Task> work, CancellationToken ct)
    {
        Gate();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var deal = await db.LendingDeals.FromSqlInterpolated($"SELECT * FROM [LendingDeals] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}").AsTracking().FirstOrDefaultAsync(ct);
            if (deal is null)
                return null;
            BringUpToDate(deal);
            await work(deal);
            deal.UpdatedAt = Now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        db.ChangeTracker.Clear();
        return await DealAsync(me, id, ct);
    }

    /// <summary>SC-LN-02: interest and fees up to this second, on drawn principal only.</summary>
    void BringUpToDate(LendingDeal deal)
    {
        if (deal.AccruedAt is not { } from || !LendingStates.IsOpen(deal.State))
            return;
        var seconds = (long)(Now - from).TotalSeconds;
        if (seconds <= 0)
            return;
        deal.SeniorDueUsd += LendingEngine.Accrue(deal.SeniorOutUsd, deal.SeniorAprBps, seconds);
        deal.JuniorDueUsd += LendingEngine.Accrue(deal.JuniorOutUsd, deal.JuniorAprBps, seconds);
        deal.FeesDueUsd += LendingEngine.Accrue(deal.SeniorOutUsd + deal.JuniorOutUsd, deal.FeesAprBps, seconds);
        deal.AccruedAt = from.AddSeconds(seconds);
    }

    async Task<IReadOnlyList<string>> BlockersAsync(LendingDeal deal, int number, decimal amount, CancellationToken ct)
    {
        var developer = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == deal.Id && x.Role == LendingRoles.Developer).Select(x => (Guid?)x.UserId).FirstOrDefaultAsync(ct);
        return LendingEngine.DrawBlockers(new DrawCheck(
            deal.State, deal.CollateralLocked, deal.CollateralFrozen,
            await db.LendingDocuments.AsNoTracking().AnyAsync(d => d.DealId == deal.Id && d.Kind == LendingDocumentKinds.SignedLoanAgreement, ct),
            number,
            await db.LendingAttestations.AsNoTracking().AnyAsync(a => a.DealId == deal.Id && a.Kind == LendingAttestationKinds.Milestone && a.DrawNumber == number, ct),
            await db.LendingProfiles.AsNoTracking().AnyAsync(p => p.UserId == developer && p.Frozen, ct),
            amount, deal.SeniorOutUsd + deal.JuniorOutUsd, deal.SeniorOutUsd + deal.JuniorOutUsd + deal.SeniorPrincipalPaidUsd + deal.JuniorPrincipalPaidUsd, deal.SeniorFundedUsd + deal.JuniorFundedUsd - deal.SeniorRedeemedUsd - deal.JuniorRedeemedUsd,
            deal.ValuationUsd, deal.ValuedAt, deal.MaxLtvBps, Now));
    }

    /// <summary>The compliance rules a token would enforce (PLT-CMP-01, PLT-CMP-03, REG-01), checked by the server for now. Null means you may lend.</summary>
    async Task<string?> WhyNotLendAsync(User me, LendingDeal deal, CancellationToken ct)
    {
        if (deal.State != LendingStates.Funding)
            return "This deal's pool isn't open.";
        if (!await KycVerifiedAsync(me.Id, ct))
            return "Verify your identity in your Wallet first.";
        var profile = await db.LendingProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == me.Id, ct);
        if (profile is null)
            return "Compliance hasn't classified you as an investor yet.";
        if (profile.Frozen)
            return "Compliance has frozen your account.";
        if (!LendingOfferingBases.Admits(deal.OfferingBasis, profile.InvestorClass))
            return $"This deal is offered under {LendingOfferingBases.Label(deal.OfferingBasis)}, which doesn't admit your investor class.";
        if (deal.BlockedJurisdictions.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(profile.Jurisdiction, StringComparer.OrdinalIgnoreCase))
            return "This deal isn't open to your country.";
        return null;
    }

    /// <summary>Your roles on a deal: those you were appointed to, and the platform's.</summary>
    async Task<IReadOnlyList<string>> RolesAsync(Guid dealId, User me, CancellationToken ct)
    {
        var roles = await db.LendingDealRoles.AsNoTracking().Where(x => x.DealId == dealId && x.UserId == me.Id).Select(x => x.Role).ToListAsync(ct);
        if (IsUnderwriter(me))
            roles.Add(LendingRoles.Underwriter);
        if (IsCompliance(me))
            roles.Add(LendingRoles.Compliance);
        return roles;
    }

    async Task RequireAsync(Guid dealId, User me, string role, CancellationToken ct)
    {
        if (!(await RolesAsync(dealId, me, ct)).Contains(role))
            throw new ChatRejectedException($"Only the deal's {LendingRoles.Label(role).ToLowerInvariant()} can do this.");
    }

    Task<bool> KycVerifiedAsync(Guid userId, CancellationToken ct) =>
        db.BankingProfiles.AsNoTracking().AnyAsync(p => p.UserId == userId && p.KycStatus == BankingProfile.Approved, ct);

    async Task<User> UserByEmailAsync(string? email, CancellationToken ct)
    {
        var address = email?.Trim();
        return (string.IsNullOrEmpty(address) ? null : await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Email == address, ct))
            ?? throw new ChatRejectedException("No one on Ndeipi has that email address.");
    }

    async Task<Dictionary<Guid, string>> NamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distinct = ids.Distinct().ToList();
        return await db.Users.AsNoTracking().Where(u => distinct.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    /// <summary>Pays one tranche's lenders in proportion to their deposits; the last takes the odd cents, so it adds up exactly.</summary>
    async Task DistributeAsync(LendingDeal deal, string tranche, decimal interest, decimal principal, CancellationToken ct)
    {
        if (interest <= 0 && principal <= 0)
            return;
        var positions = await db.LendingPositions.Where(p => p.DealId == deal.Id && p.Tranche == tranche).OrderBy(p => p.UserId).ToListAsync(ct);
        var total = positions.Sum(p => p.DepositedUsd - p.RedeemedUsd);
        var (interestLeft, principalLeft) = (interest, principal);
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            var last = i == positions.Count - 1;
            var toInterest = last ? interestLeft : Math.Round(interest * (p.DepositedUsd - p.RedeemedUsd) / total, 2);
            var toPrincipal = last ? principalLeft : Math.Round(principal * (p.DepositedUsd - p.RedeemedUsd) / total, 2);
            (interestLeft, principalLeft) = (interestLeft - toInterest, principalLeft - toPrincipal);
            p.InterestPaidUsd += toInterest;
            p.PrincipalPaidUsd += toPrincipal;
            await CreditAsync(p.UserId, toInterest + toPrincipal, ct);
        }
    }

    async Task ShareLossAsync(Guid dealId, string tranche, decimal loss, decimal funded, CancellationToken ct)
    {
        if (loss <= 0 || funded <= 0)
            return;
        var positions = await db.LendingPositions.Where(p => p.DealId == dealId && p.Tranche == tranche).OrderBy(p => p.UserId).ToListAsync(ct);
        var left = loss;
        for (var i = 0; i < positions.Count; i++)
        {
            var share = i == positions.Count - 1 ? left : Math.Round(loss * (positions[i].DepositedUsd - positions[i].RedeemedUsd) / funded, 2);
            positions[i].LossUsd += share;
            left -= share;
        }
    }

    /// <summary>When a loan closes, money that was never drawn goes back to the lenders who put it in.</summary>
    async Task ReturnUndrawnAsync(LendingDeal deal, CancellationToken ct)
    {
        var drawn = await db.LendingDraws.Where(d => d.DealId == deal.Id && d.DrawnAt != null).SumAsync(d => d.AmountUsd, ct);
        var (seniorDrawn, juniorDrawn) = LendingEngine.Allocate(drawn, deal.SeniorFundedUsd, deal.JuniorFundedUsd);
        await DistributeAsync(deal, LendingTranches.Senior, 0m, deal.SeniorFundedUsd - deal.SeniorRedeemedUsd - seniorDrawn, ct);
        await DistributeAsync(deal, LendingTranches.Junior, 0m, deal.JuniorFundedUsd - deal.JuniorRedeemedUsd - juniorDrawn, ct);
    }

    async Task CreditAsync(Guid userId, decimal amount, CancellationToken ct)
    {
        if (amount <= 0)
            return;
        await rwa.EnsureAccountAsync(userId, ct);
        await db.RwaSimAccounts.Where(a => a.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(a => a.BalanceUsd, a => a.BalanceUsd + amount), ct);
    }

    void Audit(Guid? dealId, User me, string action, string? before, string? after, string? reason) =>
        db.LendingAuditEvents.Add(new LendingAuditEvent { DealId = dealId, ActorId = me.Id, Action = action, Before = Cut(before, 200), After = Cut(after, 200), Reason = Cut(reason, LendingContract.MaxReasonLength), At = Now });

    static string? Cut(string? text, int length) => text is null || text.Length <= length ? text : text[..length];

    static string Describe(LendingProfile p) => $"{p.InvestorClass} {p.Jurisdiction} kyb={p.KybApproved} frozen={p.Frozen}";

    static string RequireReason(string? reason)
    {
        var text = reason?.Trim() ?? "";
        return text.Length is > 0 and <= LendingContract.MaxReasonLength ? text : throw new ChatRejectedException("Give a reason.");
    }

    static void Validate(LendingTermsRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Trim().Length > LendingContract.MaxNameLength || string.IsNullOrWhiteSpace(r.Location) || r.Location.Trim().Length > LendingContract.MaxNameLength)
            throw new ChatRejectedException("Give the deal a name and a location.");
        if (!LendingOfferingBases.IsValid(r.OfferingBasis))
            throw new ChatRejectedException("Choose what the deal is offered under (REG-01).");
        if (r.SeniorTargetUsd <= 0 || r.JuniorTargetUsd <= 0)
            throw new ChatRejectedException("Both tranches need a target.");
        if (r.SeniorAprBps is < 0 or > 10_000 || r.JuniorAprBps is < 0 or > 10_000 || r.FeesAprBps is < 0 or > 10_000)
            throw new ChatRejectedException("Rates must be between 0% and 100%.");
        if (r.MaxLtvBps is <= 0 or > 10_000)
            throw new ChatRejectedException("Max LTV must be between 0% and 100%.");
        if (r.BrokerFeeBps < 0 || r.PlatformFeeBps < 0 || r.BrokerFeeBps + r.PlatformFeeBps >= 10_000)
            throw new ChatRejectedException("Fees must leave something to draw.");
        if (r.ReserveTargetUsd < 0 || r.ScheduledSeniorPrincipalUsd < 0)
            throw new ChatRejectedException("Amounts can't be negative.");
        if (r.Draws is not { Count: > 0 } || r.Draws.Any(d => d.AmountUsd <= 0 || string.IsNullOrWhiteSpace(d.Milestone) || d.Milestone.Trim().Length > LendingContract.MaxNameLength))
            throw new ChatRejectedException("Plan at least one drawdown, each with an amount and a milestone.");
        if (r.Draws.Sum(d => d.AmountUsd) > r.SeniorTargetUsd + r.JuniorTargetUsd)
            throw new ChatRejectedException("The planned drawdowns add up to more than the tranches raise.");
    }

    static void Apply(LendingDeal d, LendingTermsRequest r)
    {
        (d.SeniorTargetUsd, d.JuniorTargetUsd, d.SeniorAprBps, d.JuniorAprBps, d.FeesAprBps, d.MaxLtvBps) = (r.SeniorTargetUsd, r.JuniorTargetUsd, r.SeniorAprBps, r.JuniorAprBps, r.FeesAprBps, r.MaxLtvBps);
        (d.BrokerFeeBps, d.PlatformFeeBps, d.ReserveTargetUsd, d.ScheduledSeniorPrincipalUsd, d.MaxHoldingUsd) = (r.BrokerFeeBps, r.PlatformFeeBps, r.ReserveTargetUsd, r.ScheduledSeniorPrincipalUsd, r.MaxHoldingUsd);
        (d.BlockedJurisdictions, d.RwaAssetId) = (r.BlockedJurisdictions?.Trim().ToUpperInvariant() ?? "", r.RwaAssetId);
    }

    void AddDraws(Guid dealId, LendingTermsRequest r)
    {
        for (var i = 0; i < r.Draws.Count; i++)
            db.LendingDraws.Add(new LendingDraw { DealId = dealId, Number = i + 1, AmountUsd = r.Draws[i].AmountUsd, Milestone = r.Draws[i].Milestone.Trim() });
    }
}
