using System.Globalization;

namespace NdeipiChat.Contracts;

/// <summary>
/// Ndeipi Lending (RWA Lending Platform Portal SRS, Platform &amp; Shared tab): the debt and origination
/// side of Ndeipi RWA. A deal goes from Draft to Repaid or Written-off with roles per deal; lenders
/// fund a senior and a first-loss junior tranche; the developer draws against collateral and milestone
/// attestations; repayments run through a fixed waterfall.
///
/// Everything is simulated until counsel and an audit clear it (REG-01 to REG-05, SC-UPG-01,
/// NFR-SEC-04): money is a test balance, no contract is deployed and nothing is lent. The screens are a
/// proposal, because the SRS's Broker, Lender and Borrower portal tabs haven't been supplied yet.
/// </summary>
public static class LendingContract
{
    /// <summary>Three launcher tiles from one bundle and one API (SRS section 1).</summary>
    public const string BrokerAppId = "lending-broker", LenderAppId = "lending-lender", BorrowerAppId = "lending-borrower";
    public const string BasePath = "api/lending";

    /// <summary>Who can see the app and call its API until counsel clears it.</summary>
    public const string PreviewRole = "lending-preview";

    /// <summary>Platform roles (SRS section 2), held across deals. A finance admin counts as both.</summary>
    public const string UnderwriterRole = "lending-underwriter", ComplianceRole = "lending-compliance";

    public const string SimulatedNotice = "Simulated preview. Not an offer of securities or a loan. No real money moves, no contract is deployed and nothing is lent.";

    public const int MaxNameLength = 120, MaxReasonLength = 500;

    public static string Money(decimal amount) => "$" + amount.ToString("#,0.00", CultureInfo.InvariantCulture);

    public static string Percent(int bps) => (bps / 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%";

    public static bool IsSha256(string? hash) =>
        hash is { Length: 64 } && hash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>The settlement gate (configuration "Lending:Mode").</summary>
public static class LendingModes
{
    /// <summary>Nothing can change; deals still show.</summary>
    public const string Off = "Off";

    /// <summary>Everything settles against a test balance.</summary>
    public const string Simulated = "Simulated";
}

/// <summary>The SRS's loan states, plus <see cref="Rejected"/> for an underwriter's refusal (SRS section 2).</summary>
public static class LendingStates
{
    public const string Draft = "draft", Approved = "approved", Funding = "funding", Active = "active", Grace = "grace",
        Default = "default", Recovery = "recovery", Repaid = "repaid", WrittenOff = "written_off", Rejected = "rejected";

    public static readonly IReadOnlyList<string> Path = [Draft, Approved, Funding, Active, Repaid];

    /// <summary>Who moves the deal, by the move: only these steps exist.</summary>
    static readonly Dictionary<(string From, string To), string> Moves = new()
    {
        [(Draft, Approved)] = LendingRoles.Underwriter,
        [(Draft, Rejected)] = LendingRoles.Underwriter,
        [(Approved, Funding)] = LendingRoles.Underwriter,
        [(Funding, Active)] = LendingRoles.Underwriter,
        [(Active, Grace)] = LendingRoles.Servicer,
        [(Grace, Active)] = LendingRoles.Servicer,
        [(Default, Recovery)] = LendingRoles.Trustee
    };

    /// <summary>The role that may make a move by itself, or null if it isn't a move anyone can simply make (default takes 2-of-2; repayment and write-off have their own actions).</summary>
    public static string? MoveRole(string from, string to) => Moves.GetValueOrDefault((from, to));

    public static IEnumerable<string> MovesFrom(string from) => Moves.Keys.Where(m => m.From == from).Select(m => m.To);

    public static bool IsOpen(string state) => state is Active or Grace or Default or Recovery;

    public static string Label(string state) => state switch
    {
        Draft => "Draft",
        Approved => "Approved",
        Funding => "Funding",
        Active => "Active",
        Grace => "Grace",
        Default => "Default",
        Recovery => "Recovery",
        Repaid => "Repaid",
        WrittenOff => "Written off",
        Rejected => "Rejected",
        _ => state
    };
}

/// <summary>Roles on a deal (PLT-ID-01). Underwriter and Compliance are platform roles; lenders hold positions rather than a role.</summary>
public static class LendingRoles
{
    public const string Broker = "broker", Developer = "developer", Trustee = "trustee", Servicer = "servicer",
        Inspector = "inspector", Valuer = "valuer", Underwriter = "underwriter", Compliance = "compliance";

    /// <summary>Who a deal needs named before its pool opens (SRS assumptions).</summary>
    public static readonly IReadOnlyList<string> RequiredToOpen = [Developer, Trustee, Servicer, Inspector];

    public static readonly IReadOnlyList<string> Appointed = [Broker, Developer, Trustee, Servicer, Inspector, Valuer];

    public static string Label(string role) => role switch
    {
        Broker => "Broker",
        Developer => "Project developer",
        Trustee => "Security trustee",
        Servicer => "Loan servicer",
        Inspector => "Inspector",
        Valuer => "Valuer",
        Underwriter => "Underwriter",
        Compliance => "Compliance",
        _ => role
    };
}

public static class LendingTranches
{
    public const string Senior = "senior", Junior = "junior";

    public static bool IsValid(string? tranche) => tranche is Senior or Junior;
}

/// <summary>PLT-ID-04: how an investor is classified; a deal's offering basis says which it admits.</summary>
public static class LendingInvestorClasses
{
    public const string Unclassified = "unclassified", Retail = "retail", Accredited = "accredited", RegS = "reg_s", JamaicanSophisticated = "jm_sophisticated";

    public static readonly IReadOnlyList<(string Code, string Label)> All =
        [(Accredited, "Accredited"), (RegS, "Non-US (Reg S)"), (JamaicanSophisticated, "Jamaican sophisticated investor"), (Retail, "Retail")];

    public static bool IsValid(string? code) => All.Any(c => c.Code == code);
}

/// <summary>REG-01: what a deal is offered under, and so who may lend to it.</summary>
public static class LendingOfferingBases
{
    public const string RegD506c = "reg_d_506c", RegS = "reg_s", JamaicaFscExemption = "jm_fsc_exemption";

    public static readonly IReadOnlyList<(string Code, string Label)> All =
        [(RegD506c, "Reg D 506(c)"), (RegS, "Reg S"), (JamaicaFscExemption, "Jamaican FSC exemption")];

    public static bool IsValid(string? code) => All.Any(b => b.Code == code);

    public static string Label(string? code) => All.FirstOrDefault(b => b.Code == code).Label ?? "None";

    public static bool Admits(string basis, string investorClass) => (basis, investorClass) switch
    {
        (RegD506c, LendingInvestorClasses.Accredited) => true,
        (RegS, LendingInvestorClasses.RegS) => true,
        (JamaicaFscExemption, LendingInvestorClasses.JamaicanSophisticated) => true,
        _ => false
    };
}

public static class LendingDocumentKinds
{
    public const string SignedLoanAgreement = "signed_loan_agreement", TitleDeed = "title_deed", Permit = "permit", Other = "other";

    public static readonly IReadOnlyList<(string Code, string Label)> All =
        [(SignedLoanAgreement, "Signed loan agreement"), (TitleDeed, "Title deed"), (Permit, "Permit"), (Other, "Other")];

    public static bool IsValid(string? code) => All.Any(k => k.Code == code);

    public static string Label(string code) => All.FirstOrDefault(k => k.Code == code).Label ?? code;
}

public static class LendingAttestationKinds
{
    /// <summary>A signed milestone (PLT-ORC-01), by the Inspector; releases a later draw.</summary>
    public const string Milestone = "milestone";

    /// <summary>An appraisal, by the Valuer; sets LTV and goes stale after 12 months (PLT-ORC-02).</summary>
    public const string Valuation = "valuation";

    /// <summary>REG-05: the charge over the land is registered in the Trustee's favour; no pool opens without it.</summary>
    public const string LandCharge = "land_charge";

    public static string? AttestorRole(string kind) => kind switch
    {
        Milestone => LendingRoles.Inspector,
        Valuation => LendingRoles.Valuer,
        LandCharge => LendingRoles.Trustee,
        _ => null
    };

    public static string Label(string kind) => kind switch
    {
        Milestone => "Milestone",
        Valuation => "Valuation",
        LandCharge => "Land charge registered",
        _ => kind
    };
}

/// <summary>What arrives at the waterfall: the cash, and what each step is owed.</summary>
/// <param name="ScheduledSeniorPrincipal">Step 3: what's scheduled now, at most what's outstanding.</param>
public sealed record WaterfallInput(
    decimal Cash,
    decimal FeesDue,
    decimal SeniorInterestDue,
    decimal ScheduledSeniorPrincipal,
    decimal SeniorPrincipalOutstanding,
    decimal JuniorInterestDue,
    decimal JuniorPrincipalOutstanding,
    decimal ReserveBalance,
    decimal ReserveTarget,
    bool InDefault);

/// <summary>What each step of the waterfall paid, in payment order.</summary>
/// <param name="SeniorPrincipalSweep">In default only: what went to senior principal ahead of junior principal.</param>
/// <param name="Residual">Step 7: what's left for the borrower.</param>
public sealed record WaterfallResult(
    decimal Fees,
    decimal SeniorInterest,
    decimal SeniorPrincipal,
    decimal JuniorInterest,
    decimal SeniorPrincipalSweep,
    decimal JuniorPrincipal,
    decimal Reserve,
    decimal Residual);

/// <summary>Everything that decides whether a draw may go ahead (SC-LN-01, PLT-ORC-02).</summary>
public sealed record DrawCheck(
    string State,
    bool CollateralLocked,
    bool CollateralFrozen,
    bool SignedLoanAgreementOnRecord,
    int DrawNumber,
    bool MilestoneAttested,
    bool DeveloperFrozen,
    decimal Amount,
    decimal DrawnPrincipal,
    decimal DrawnToDate,
    decimal Funded,
    decimal? ValuationUsd,
    DateTimeOffset? ValuedAt,
    int MaxLtvBps,
    DateTimeOffset Now);

/// <summary>
/// The contract engine's arithmetic, with no storage in it: tranches, the waterfall, accrual, LTV and
/// health. It mirrors what LoanManager, TrancheVault, RepaymentWaterfall and FeeSplitter would do on
/// chain (SRS section 4), so the app and the server show the same numbers.
/// </summary>
public static class LendingEngine
{
    public const long SecondsPerYear = 31_536_000;

    /// <summary>SC-LN-02: per-second interest on drawn principal only.</summary>
    public static decimal Accrue(decimal drawnPrincipal, int aprBps, long seconds) =>
        seconds <= 0 || drawnPrincipal <= 0 ? 0m : drawnPrincipal * aprBps / 10_000m * seconds / SecondsPerYear;

    /// <summary>Drawn principal over the latest signed valuation; null with no valuation yet.</summary>
    public static decimal? Ltv(decimal drawnPrincipal, decimal? valuationUsd) =>
        valuationUsd is > 0 ? drawnPrincipal / valuationUsd.Value : null;

    /// <summary>Max allowed LTV over current LTV; null when nothing is drawn. Below 1 is a margin event.</summary>
    public static decimal? HealthFactor(int maxLtvBps, decimal drawnPrincipal, decimal? valuationUsd) =>
        Ltv(drawnPrincipal, valuationUsd) is { } ltv && ltv > 0 ? maxLtvBps / 10_000m / ltv : null;

    /// <summary>PLT-ORC-02: a valuation older than 12 months blocks new borrowing.</summary>
    public static bool ValuationStale(DateTimeOffset? valuedAt, DateTimeOffset now) =>
        valuedAt is null || now > valuedAt.Value.AddMonths(12);

    /// <summary>SC-VLT-02: losses take junior equity first; senior is touched only once junior is at zero.</summary>
    public static (decimal Junior, decimal Senior) ApplyLoss(decimal loss, decimal juniorEquity, decimal seniorEquity)
    {
        var junior = Math.Min(Math.Max(loss, 0m), Math.Max(juniorEquity, 0m));
        var senior = Math.Min(Math.Max(loss, 0m) - junior, Math.Max(seniorEquity, 0m));
        return (junior, senior);
    }

    /// <summary>What a tranche share is worth, starting at 1: deposits less the losses it has taken.</summary>
    public static decimal SharePrice(decimal deposited, decimal lossTaken) =>
        deposited <= 0 ? 1m : Math.Max(deposited - lossTaken, 0m) / deposited;

    /// <summary>A draw shared across the tranches in proportion to what each funded; junior takes the cents left over.</summary>
    public static (decimal Senior, decimal Junior) Allocate(decimal amount, decimal seniorFunded, decimal juniorFunded)
    {
        var total = seniorFunded + juniorFunded;
        if (total <= 0)
            return (0m, 0m);
        var senior = Math.Round(amount * seniorFunded / total, 2);
        return (senior, amount - senior);
    }

    /// <summary>SC-FEE-01: broker and platform fees come off each draw at source.</summary>
    public static (decimal Broker, decimal Platform, decimal Net) SplitFees(decimal amount, int brokerBps, int platformBps)
    {
        var broker = Math.Round(amount * brokerBps / 10_000m, 2);
        var platform = Math.Round(amount * platformBps / 10_000m, 2);
        return (broker, platform, amount - broker - platform);
    }

    /// <summary>
    /// The SRS's fixed order: fees, senior interest, scheduled senior principal, junior interest, junior
    /// principal, reserve top-up, then the residual to the borrower.
    ///
    /// In default the SRS sends the residual to senior principal, but read literally that pays junior
    /// principal first, which would let senior lose money while junior is repaid (against SC-VLT-02).
    /// So in default senior principal is swept ahead of junior principal. Counsel should confirm this.
    /// </summary>
    public static WaterfallResult RunWaterfall(WaterfallInput i)
    {
        var cash = Math.Max(i.Cash, 0m);
        decimal Pay(decimal due)
        {
            var paid = Math.Min(cash, Math.Max(due, 0m));
            cash -= paid;
            return paid;
        }

        var fees = Pay(i.FeesDue);
        var seniorInterest = Pay(i.SeniorInterestDue);
        var seniorPrincipal = Pay(Math.Min(i.ScheduledSeniorPrincipal, i.SeniorPrincipalOutstanding));
        var juniorInterest = Pay(i.JuniorInterestDue);
        var sweep = i.InDefault ? Pay(i.SeniorPrincipalOutstanding - seniorPrincipal) : 0m;
        var juniorPrincipal = Pay(i.JuniorPrincipalOutstanding);
        var reserve = Pay(i.ReserveTarget - i.ReserveBalance);
        return new WaterfallResult(fees, seniorInterest, seniorPrincipal, juniorInterest, sweep, juniorPrincipal, reserve, cash);
    }

    /// <summary>Why a draw can't go ahead, in words for the person trying; empty when it can.</summary>
    public static IReadOnlyList<string> DrawBlockers(DrawCheck c)
    {
        var blockers = new List<string>();
        if (c.State != LendingStates.Active)
            blockers.Add("The loan isn't active, so drawdowns are frozen.");
        if (c.DeveloperFrozen)
            blockers.Add("Compliance has frozen the developer.");
        if (!c.CollateralLocked)
            blockers.Add("The collateral isn't locked.");
        if (c.CollateralFrozen)
            blockers.Add("The collateral is frozen.");
        if (!c.SignedLoanAgreementOnRecord)
            blockers.Add("No signed loan agreement hash is on record.");
        if (c.DrawNumber >= 2 && !c.MilestoneAttested)
            blockers.Add($"Tranche {c.DrawNumber} needs a milestone attestation from the inspector.");
        if (ValuationStale(c.ValuedAt, c.Now))
            blockers.Add("The valuation is missing or more than 12 months old.");
        else if (Ltv(c.DrawnPrincipal + c.Amount, c.ValuationUsd) > c.MaxLtvBps / 10_000m)
            blockers.Add($"This draw would take LTV past the {LendingContract.Percent(c.MaxLtvBps)} limit.");
        if (c.DrawnToDate + c.Amount > c.Funded)
            blockers.Add("The tranches haven't funded enough for this draw.");
        return blockers;
    }
}

public sealed record LendingMeDto(
    string Mode,
    bool KycVerified,
    bool IsUnderwriter,
    bool IsCompliance,
    decimal SimBalanceUsd,
    string InvestorClass,
    string? Jurisdiction,
    bool KybApproved,
    bool Frozen,
    string Notice);

public sealed record LendingDealSummaryDto(
    Guid Id,
    string Code,
    string Name,
    string Location,
    string State,
    string OfferingBasis,
    decimal SeniorTargetUsd,
    decimal JuniorTargetUsd,
    decimal SeniorFundedUsd,
    decimal JuniorFundedUsd,
    IReadOnlyList<string> MyRoles,
    decimal MyDepositedUsd);

/// <summary>The terms a broker proposes and an underwriter finalises. They can't change once the pool opens (SC-FAC-01).</summary>
public sealed record LendingTermsRequest(
    string Name,
    string Location,
    string OfferingBasis,
    decimal SeniorTargetUsd,
    decimal JuniorTargetUsd,
    int SeniorAprBps,
    int JuniorAprBps,
    int FeesAprBps,
    int MaxLtvBps,
    int BrokerFeeBps,
    int PlatformFeeBps,
    decimal ReserveTargetUsd,
    decimal ScheduledSeniorPrincipalUsd,
    decimal? MaxHoldingUsd,
    string? BlockedJurisdictions,
    Guid? RwaAssetId,
    IReadOnlyList<LendingDrawPlan> Draws);

public sealed record LendingDrawPlan(decimal AmountUsd, string Milestone);

public sealed record LendingDrawDto(int Number, decimal AmountUsd, string Milestone, bool MilestoneAttested, DateTimeOffset? DrawnAt, decimal? BrokerFeeUsd, decimal? PlatformFeeUsd, decimal? NetUsd, IReadOnlyList<string> Blockers);

public sealed record LendingTrancheDto(string Tranche, decimal TargetUsd, decimal FundedUsd, int AprBps, decimal PrincipalOutstandingUsd, decimal InterestDueUsd, decimal InterestPaidUsd, decimal PrincipalPaidUsd, decimal LossUsd, decimal SharePrice);

public sealed record LendingPositionDto(string Tranche, decimal DepositedUsd, decimal InterestPaidUsd, decimal PrincipalPaidUsd, decimal LossUsd, decimal RedeemedUsd = 0m);

public sealed record LendingParticipantDto(string Role, Guid UserId, string Name);

public sealed record LendingDocumentDto(Guid Id, string Name, string Kind, int Version, string Sha256, string UploadedBy, DateTimeOffset At);

public sealed record LendingAttestationDto(Guid Id, string Kind, string Attestor, string ReportSha256, decimal? ValueUsd, int? DrawNumber, DateTimeOffset At);

public sealed record LendingPaymentDto(Guid Id, string Reference, decimal AmountUsd, WaterfallResult Paid, string RecordedBy, DateTimeOffset At);

public sealed record LendingDefaultVoteDto(string Role, string Name, string Reason, DateTimeOffset At);

/// <param name="MyRoles">Your roles on this deal, including platform roles.</param>
/// <param name="CanLend">Null when you may lend; otherwise why not.</param>
/// <param name="HealthFactor">Null when nothing is drawn.</param>
public sealed record LendingDealDto(
    Guid Id,
    string Code,
    string Name,
    string Location,
    string State,
    string OfferingBasis,
    LendingTermsRequest Terms,
    IReadOnlyList<string> MyRoles,
    IReadOnlyList<string> NextStates,
    string? CanLend,
    IReadOnlyList<LendingParticipantDto> Participants,
    IReadOnlyList<LendingTrancheDto> Tranches,
    IReadOnlyList<LendingPositionDto> MyPositions,
    IReadOnlyList<LendingDrawDto> Draws,
    IReadOnlyList<LendingDocumentDto> Documents,
    IReadOnlyList<LendingAttestationDto> Attestations,
    IReadOnlyList<LendingPaymentDto> Payments,
    IReadOnlyList<LendingDefaultVoteDto> DefaultVotes,
    decimal DrawnPrincipalUsd,
    decimal FeesDueUsd,
    decimal ReserveUsd,
    decimal? ValuationUsd,
    DateTimeOffset? ValuedAt,
    bool ValuationStale,
    decimal? Ltv,
    decimal? HealthFactor,
    bool MarginEvent,
    bool CollateralLocked,
    bool CollateralFrozen,
    bool LandChargeRegistered,
    DateTimeOffset? SubmittedAt = null,
    string? ReturnReason = null,
    IReadOnlyList<LendingRedemptionDto>? MyRedemptions = null);

public sealed record LendingAppointRequest(string Role, string Email);

public sealed record LendingTransitionRequest(string To, string? Reason);

public sealed record LendingDepositRequest(string Tranche, decimal AmountUsd, Guid RequestId);

public sealed record LendingDocumentRequest(string Name, string Kind, string Sha256);

public sealed record LendingAttestationRequest(string Kind, string ReportSha256, decimal? ValueUsd, int? DrawNumber);

/// <param name="Reference">PLT-PAY-02: the bank or booking reference, one record per transfer.</param>
public sealed record LendingPaymentRequest(string Reference, decimal AmountUsd);

public sealed record LendingReasonRequest(string Reason);

/// <summary>PLT-ID-04, PLT-CMP-02: what Compliance records about someone.</summary>
public sealed record LendingProfileRequest(string Email, string InvestorClass, string Jurisdiction, bool KybApproved, bool Frozen, string Reason);

public sealed record LendingTopUpRequest(string Email, decimal AmountUsd);

public sealed record LendingAuditDto(long Id, DateTimeOffset At, string Actor, string Action, string? Before, string? After, string? Reason);

// ---- The portals (drafted requirements: docs/lending-portals-requirements-DRAFT.md)

public static class LendingRedemptions
{
    /// <summary>LND-FR-07: a window opens this long after a request, and stays open this long.</summary>
    public const int NoticeDays = 7, WindowDays = 14;

    public const string Requested = "requested", Redeemed = "redeemed";
}

public sealed record LendingChecklistItem(string Label, bool Done);

/// <summary>BRK-FR-05, BRK-FR-07: one deal in a broker's pipeline.</summary>
public sealed record BrokerDealDto(LendingDealSummaryDto Deal, IReadOnlyList<LendingChecklistItem> Checklist, DateTimeOffset? SubmittedAt, string? ReturnReason, decimal FeesEarnedUsd, IReadOnlyList<decimal> FeePerDraw);

/// <param name="Queue">For underwriters: deals submitted and waiting for a decision.</param>
public sealed record BrokerPipelineDto(IReadOnlyList<BrokerDealDto> Deals, IReadOnlyList<LendingDealSummaryDto> Queue, decimal FeesEarnedUsd);

/// <summary>LND-FR-05: one stake in one tranche.</summary>
public sealed record LenderPositionDto(Guid DealId, string DealName, string State, string Tranche, decimal DepositedUsd, decimal RedeemedUsd, decimal InterestPaidUsd, decimal PrincipalPaidUsd, decimal LossUsd, decimal SharePrice, decimal ValueUsd);

/// <param name="CanLend">Null when you may lend; otherwise why not (LND-FR-02).</param>
public sealed record LenderPoolDto(LendingDealSummaryDto Deal, int SeniorAprBps, int JuniorAprBps, string? CanLend);

public sealed record LenderPortfolioDto(IReadOnlyList<LenderPositionDto> Positions, IReadOnlyList<LenderPoolDto> OpenPools, decimal DepositedUsd, decimal InterestPaidUsd, decimal PrincipalPaidUsd, decimal LossUsd, decimal ValueUsd);

/// <summary>BOR-FR-01: one loan a developer is on.</summary>
public sealed record BorrowerLoanDto(LendingDealSummaryDto Deal, decimal DrawnPrincipalUsd, decimal DueNowUsd, decimal? HealthFactor, bool MarginEvent, bool CollateralLocked, LendingDrawDto? NextDraw);

public sealed record BorrowerLoansDto(IReadOnlyList<BorrowerLoanDto> Loans, bool KybApproved);

public sealed record LendingRedemptionDto(Guid Id, string Tranche, decimal AmountUsd, string Status, DateTimeOffset WindowOpensAt, DateTimeOffset WindowClosesAt, DateTimeOffset? RedeemedAt);

public sealed record LendingRedeemRequest(string Tranche, decimal AmountUsd);

public sealed record LendingRepayRequest(decimal AmountUsd);

public sealed record LendingInspectionRequest(int DrawNumber);

public sealed record LendingNoticeDto(long Id, Guid? DealId, string Text, DateTimeOffset At, bool Read);
