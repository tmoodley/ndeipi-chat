namespace NdeipiChat.Contracts;

/// <summary>
/// The Finance sub-app, starting with the Absa Artisanal Gold Mining Loan (Migodi-Auric, "Application
/// screens", 1 October 2026): an ASM group checks it's eligible (FR-ELG), describes itself and where
/// it is (FR-GRP), picks the equipment and running costs to finance (FR-LTY, FR-TRM), uploads its
/// documents (FR-DOC) and submits (FR-SUB). The application then goes through the committees for its
/// area in turn (FR-WF), is collected at Absa (FR-DSB), and is repaid with ore receipts (FR-REP); the
/// applicant follows each step live (FR-NTF).
/// </summary>
public static class FinanceContract
{
    public const string AppId = "finance";
    public const string BasePath = "api/finance";

    /// <summary>Who sets up committees, equipment prices and constituencies, and can act at any stage.</summary>
    public const string AdminRole = "admin";

    /// <summary>What loan values are in: Zambian kwacha.</summary>
    public const string Currency = "ZMW";

    /// <summary>A document may be a photo or PDF up to this size.</summary>
    public const long MaxDocumentBytes = 10 * 1024 * 1024;

    /// <summary>FR-TRM: how many months of running costs may be financed.</summary>
    public static readonly IReadOnlyList<int> RunningMonths = [1, 2, 3, 4, 5, 6];

    /// <summary>FR-TRM: interest by risk profile and grace period, set by Absa in its final valuation.</summary>
    public const int MinInterestPercent = 5, MaxInterestPercent = 10, MinGraceMonths = 1, MaxGraceMonths = 3;

    public const int MaxNameLength = 120;
    public const int MaxPlaceLength = 80;
    public const int MaxCommentLength = 1000;

    /// <summary>Live updates for an application: its applicant and its reviewers. "finance:{id:N}".</summary>
    public static string ApplicationTopic(Guid applicationId) => $"finance:{applicationId:N}";

    /// <summary>Zambia's provinces, for the location step and committee areas.</summary>
    public static readonly IReadOnlyList<string> Provinces =
        ["Central", "Copperbelt", "Eastern", "Luapula", "Lusaka", "Muchinga", "Northern", "North-Western", "Southern", "Western"];
}

/// <summary>A choice in a list: its code (stored) and what people see.</summary>
public sealed record FinanceOption(string Code, string Label);

/// <summary>FR-ELG-02: where a group is registered. Any of these meets the criterion.</summary>
public static class RegistrationBodies
{
    public const string Cooperatives = "cooperatives";
    public const string Pacra = "pacra";
    public const string Societies = "societies";

    public static readonly IReadOnlyList<FinanceOption> All =
    [
        new(Cooperatives, "Registrar of Cooperative Societies"),
        new(Pacra, "Patents and Companies Registration Agency (PACRA)"),
        new(Societies, "Registrar of Societies")
    ];
}

/// <summary>FR-ELG-03: the mining licence behind the proposal. Any of these meets the criterion.</summary>
public static class LicenceTypes
{
    public const string Artisanal = "artisanal";
    public const string SmallScale = "small_scale";

    public static readonly IReadOnlyList<FinanceOption> All =
    [
        new(Artisanal, "Artisanal Mining Licence"),
        new(SmallScale, "Small-scale Mining Licence")
    ];
}

/// <summary>FR-GRP-02: what kind of group is applying.</summary>
public static class GroupTypes
{
    public static readonly IReadOnlyList<FinanceOption> All =
    [
        new("cooperative", "Cooperative Society"),
        new("club", "Mining Club"),
        new("association", "Association"),
        new("company", "Company"),
        new("group", "Other organised group")
    ];
}

/// <summary>FR-LTY-01: what the group's cluster does, which decides the equipment it can finance.</summary>
public static class ClusterTypes
{
    public const string Production = "production";
    public const string Processing = "processing";

    public static readonly IReadOnlyList<FinanceOption> All = [new(Production, "Production"), new(Processing, "Processing")];
}

/// <summary>FR-DOC-01: the four documents every application needs.</summary>
public static class LoanDocumentKinds
{
    public const string Registration = "registration";
    public const string Licence = "licence";
    public const string Bank = "bank";
    public const string Proposal = "proposal";

    public static readonly IReadOnlyList<(string Kind, string Label, string Hint)> All =
    [
        (Registration, "Registration certificate", "From your registration body"),
        (Licence, "Mining licence", "The licence behind your proposal"),
        (Bank, "Proof of bank account", "Bank letter or statement"),
        (Proposal, "Business / project proposal", "Based on your mining licence")
    ];

    public static string Label(string kind) => All.FirstOrDefault(d => d.Kind == kind).Label ?? kind;
}

/// <summary>
/// FR-WF: the stages an application goes through, in order. Each committee stage approves it on, sends
/// it back to the applicant, or declines it; the last, at Absa, records that the loan was collected.
/// </summary>
public static class LoanStages
{
    public const string Village = "village";
    public const string Ward = "ward";
    public const string Chief = "chief";
    public const string Constituency = "constituency";
    public const string Province = "province";
    public const string Absa = "absa";

    public static readonly IReadOnlyList<string> Order = [Village, Ward, Chief, Constituency, Province, Absa];

    public static string Label(string stage) => stage switch
    {
        Village => "Village Productivity Committee",
        Ward => "Ward Development Committee",
        Chief => "Chief endorsement",
        Constituency => "Constituency CDF Committee",
        Province => "Provincial CDF Committee",
        Absa => "Collect at your nearest Absa branch",
        _ => stage
    };

    public static string Hint(string stage) => stage switch
    {
        Village => "Chaired by your headman",
        Chief => "After the ward appraisal",
        Absa => "Or an Absa Agency on your Cathedral Route",
        _ => ""
    };

    /// <summary>The stage after this one, or null after Absa.</summary>
    public static string? Next(string stage) => Position(stage) is var i and >= 0 && i + 1 < Order.Count ? Order[i + 1] : null;

    /// <summary>Where a stage comes in the order, or -1.</summary>
    public static int Position(string? stage) => stage is null ? -1 : Order.ToList().IndexOf(stage);
}

public static class LoanStatuses
{
    /// <summary>Being filled in: only the applicant sees it.</summary>
    public const string Draft = "draft";

    /// <summary>With a committee (<see cref="LoanApplicationDto.Stage"/>).</summary>
    public const string InReview = "in_review";

    /// <summary>Sent back to the applicant to change and submit again.</summary>
    public const string Returned = "returned";

    public const string Declined = "declined";

    /// <summary>Through every committee: waiting to be collected at Absa.</summary>
    public const string Approved = "approved";

    /// <summary>Collected at Absa: repaid from here with ore receipts.</summary>
    public const string Collected = "collected";
}

public static class LoanDecisions
{
    public const string Approve = "approve";
    public const string Return = "return";
    public const string Decline = "decline";
}

/// <summary>FR-REP: where ore receipts are issued, managed by Swaptor Commodity Exchange with Absa Agencies.</summary>
public static class RepaymentPoints
{
    public static readonly IReadOnlyList<string> All =
        ["Pit Bin (Pit OBW)", "Cluster Bin", "LAMLMP / POMCLP Bin", "Processing Plant Bin", "Offtaker / Pre-Financier Bin"];
}

/// <summary>FR-ELG: the four criteria. A group must meet all of them to go on.</summary>
public sealed record EligibilityDto(bool? ZambianOwned, string? RegistrationBody, string? LicenceType, bool? HasBankAccount)
{
    /// <summary>What isn't met yet, in the order asked. Empty when the group is eligible.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (ZambianOwned != true)
            problems.Add("The group must be a legally registered entity fully owned by Zambians.");
        if (!RegistrationBodies.All.Any(b => b.Code == RegistrationBody))
            problems.Add("The group must be registered with the Registrar of Cooperative Societies, PACRA or the Registrar of Societies.");
        if (!LicenceTypes.All.Any(l => l.Code == LicenceType))
            problems.Add("The proposal must be backed by an artisanal or small-scale mining licence.");
        if (HasBankAccount != true)
            problems.Add("The group must have a bank account.");
        return problems;
    }
}

/// <summary>One piece of equipment chosen, to hire (monthly) or buy.</summary>
public sealed record LoanItemRequest(Guid EquipmentId, bool Buy);

/// <summary>A draft application, saved step by step. Everything may be blank until it's submitted.</summary>
public sealed record SaveLoanApplicationRequest(
    EligibilityDto Eligibility,
    string? GroupName,
    string? GroupType,
    string? RegistrationNumber,
    string? Province,
    string? Constituency,
    string? Ward,
    string? Village,
    string? ClusterType,
    IReadOnlyList<LoanItemRequest>? Items,
    int? RunningMonths);

public sealed record SubmitLoanRequest(bool Declared);

public sealed record LoanDecisionRequest(string Decision, string? Comment, string? Reference = null);

/// <summary>A line of the estimate; Amount is null while that price is still to be confirmed.</summary>
public sealed record LoanEstimateLineDto(string Label, decimal? Amount);

/// <param name="Complete">Every chosen item has its prices set; otherwise the total leaves those out.</param>
public sealed record LoanEstimateDto(IReadOnlyList<LoanEstimateLineDto> Lines, decimal Total, string Currency, bool Complete);

public sealed record LoanItemDto(Guid EquipmentId, string Name, bool Buy);

public sealed record LoanDocumentDto(string Kind, string Label, string Hint, string? FileName, long? Size, DateTimeOffset? UploadedAt);

/// <param name="State">"done", "current", "waiting", "returned" or "declined".</param>
/// <param name="Committee">The committee (or Absa branch) handling it for this application's area, if one is set up.</param>
public sealed record LoanStageDto(
    string Stage,
    string Label,
    string Hint,
    string State,
    string? Committee,
    string? DecidedBy,
    string? Comment,
    DateTimeOffset? DecidedAt);

/// <param name="Reference">Quoted to committees and at Absa, e.g. "GL-7F3K2Q".</param>
/// <param name="Stage">Where it is while in review (or where it was returned from).</param>
/// <param name="CanEdit">The applicant may change it: a draft, or returned to them.</param>
/// <param name="CanDecide">The signed-in user sits on the committee for its current stage (or is an admin).</param>
/// <param name="ReturnedComment">Why it was sent back, while it's returned.</param>
public sealed record LoanApplicationDto(
    Guid Id,
    string Reference,
    string Status,
    string? Stage,
    EligibilityDto Eligibility,
    string? GroupName,
    string? GroupType,
    string? RegistrationNumber,
    string? Province,
    string? Constituency,
    string? Ward,
    string? Village,
    string? ClusterType,
    IReadOnlyList<LoanItemDto> Items,
    int RunningMonths,
    LoanEstimateDto Estimate,
    IReadOnlyList<LoanDocumentDto> Documents,
    IReadOnlyList<LoanStageDto> Stages,
    UserDto Applicant,
    bool IsMine,
    bool CanEdit,
    bool CanDecide,
    string? ReturnedComment,
    string? CollectionReference,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SubmittedAt);

/// <summary>An application in a list: the applicant's own, or a committee's queue.</summary>
public sealed record LoanSummaryDto(
    Guid Id,
    string Reference,
    string Status,
    string? Stage,
    string? GroupName,
    string? Place,
    decimal Estimate,
    string Applicant,
    DateTimeOffset UpdatedAt);

/// <param name="ToReview">Applications waiting on a committee they sit on.</param>
public sealed record FinanceMeDto(bool IsAdmin, IReadOnlyList<LoanCommitteeDto> Committees, int ToReview);

/// <summary>FR-GRP-04: who an application from this place goes to first.</summary>
public sealed record LoanRouteDto(IReadOnlyList<LoanStageDto> Stages);

public sealed record LoanEquipmentDto(
    Guid Id,
    string Name,
    string Description,
    string ClusterType,
    string? Mtp,
    decimal? HirePerMonth,
    decimal? BuyPrice,
    decimal? RunningPerMonth,
    bool Active,
    int Order);

public sealed record SaveEquipmentRequest(
    string Name,
    string Description,
    string ClusterType,
    string? Mtp,
    decimal? HirePerMonth,
    decimal? BuyPrice,
    decimal? RunningPerMonth,
    bool Active,
    int Order);

/// <summary>
/// A committee (or Absa branch) for one stage, covering an area: a province, narrowed by whichever of
/// constituency, ward and village are set. The most specific one covering an application handles it.
/// </summary>
public sealed record LoanCommitteeDto(
    Guid Id,
    string Stage,
    string Name,
    string Province,
    string? Constituency,
    string? Ward,
    string? Village,
    IReadOnlyList<UserDto> Members);

public sealed record SaveCommitteeRequest(
    string Stage,
    string Name,
    string Province,
    string? Constituency,
    string? Ward,
    string? Village,
    IReadOnlyList<Guid> MemberIds);

/// <summary>A province's constituencies, for the location step. Replaces the whole list.</summary>
public sealed record SaveConstituenciesRequest(string Province, IReadOnlyList<string> Names);

/// <summary>Opens a document for a few minutes without the sign-in token, e.g. in a new tab.</summary>
public sealed record LoanDocumentLinkDto(string Url);
