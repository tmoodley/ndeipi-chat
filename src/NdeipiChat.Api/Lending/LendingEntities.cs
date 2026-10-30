namespace NdeipiChat.Api.Lending;

/// <summary>A loan to one project, with its terms and running balances (mirrors ProjectRegistry, LoanManager and the tranche vaults).</summary>
public sealed class LendingDeal
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Location { get; set; }

    /// <summary>A <see cref="NdeipiChat.Contracts.LendingStates"/> value.</summary>
    public required string State { get; set; }

    public required string OfferingBasis { get; set; }

    /// <summary>The villa whose fractional holders rank below these lenders, if any.</summary>
    public Guid? RwaAssetId { get; set; }

    public decimal SeniorTargetUsd { get; set; }
    public decimal JuniorTargetUsd { get; set; }
    public int SeniorAprBps { get; set; }
    public int JuniorAprBps { get; set; }

    /// <summary>Servicer, trustee and platform fees, accruing on drawn principal; step 1 of the waterfall.</summary>
    public int FeesAprBps { get; set; }

    public int MaxLtvBps { get; set; }
    public int BrokerFeeBps { get; set; }
    public int PlatformFeeBps { get; set; }
    public decimal ReserveTargetUsd { get; set; }

    /// <summary>What step 3 of the waterfall treats as due at each payment.</summary>
    public decimal ScheduledSeniorPrincipalUsd { get; set; }

    public decimal? MaxHoldingUsd { get; set; }

    /// <summary>Upper-case country codes, comma-separated (PLT-CMP-03).</summary>
    public string BlockedJurisdictions { get; set; } = "";

    public decimal SeniorRedeemedUsd { get; set; }
    public decimal JuniorRedeemedUsd { get; set; }

    /// <summary>BRK-FR-06: sent to the underwriter. Cleared if they return it, with their reason.</summary>
    public DateTimeOffset? SubmittedAt { get; set; }

    public string? ReturnReason { get; set; }

    public decimal SeniorFundedUsd { get; set; }
    public decimal JuniorFundedUsd { get; set; }
    public decimal SeniorOutUsd { get; set; }
    public decimal JuniorOutUsd { get; set; }
    public decimal SeniorDueUsd { get; set; }
    public decimal JuniorDueUsd { get; set; }
    public decimal FeesDueUsd { get; set; }
    public decimal SeniorInterestPaidUsd { get; set; }
    public decimal JuniorInterestPaidUsd { get; set; }
    public decimal SeniorPrincipalPaidUsd { get; set; }
    public decimal JuniorPrincipalPaidUsd { get; set; }
    public decimal SeniorLossUsd { get; set; }
    public decimal JuniorLossUsd { get; set; }
    public decimal ReserveUsd { get; set; }
    public decimal? ValuationUsd { get; set; }
    public DateTimeOffset? ValuedAt { get; set; }
    public bool CollateralLocked { get; set; }
    public bool CollateralFrozen { get; set; }
    public bool LandChargeRegistered { get; set; }

    /// <summary>SC-FEE-01: who the broker fee goes to, fixed at approval.</summary>
    public Guid? BrokerFeeUserId { get; set; }

    /// <summary>When interest was last brought up to date. Null until the loan is active.</summary>
    public DateTimeOffset? AccruedAt { get; set; }

    public Guid CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One planned drawdown and, once made, what it paid out.</summary>
public sealed class LendingDraw
{
    public Guid DealId { get; set; }
    public int Number { get; set; }
    public decimal AmountUsd { get; set; }
    public required string Milestone { get; set; }
    public DateTimeOffset? DrawnAt { get; set; }
    public decimal? BrokerFeeUsd { get; set; }
    public decimal? PlatformFeeUsd { get; set; }
    public decimal? NetUsd { get; set; }
}

/// <summary>Who holds an appointed role on a deal (PLT-ID-01): one person per role.</summary>
public sealed class LendingDealRole
{
    public Guid DealId { get; set; }
    public required string Role { get; set; }
    public Guid UserId { get; set; }
}

/// <summary>A lender's stake in one tranche (TrancheVault mirror). Shares start at 1 per dollar.</summary>
public sealed class LendingPosition
{
    public Guid DealId { get; set; }
    public Guid UserId { get; set; }
    public required string Tranche { get; set; }
    public decimal DepositedUsd { get; set; }
    public decimal RedeemedUsd { get; set; }
    public decimal InterestPaidUsd { get; set; }
    public decimal PrincipalPaidUsd { get; set; }
    public decimal LossUsd { get; set; }
}

/// <summary>One deposit request, so a retry doesn't deposit twice.</summary>
public sealed class LendingDeposit
{
    public Guid UserId { get; set; }
    public Guid RequestId { get; set; }
    public Guid DealId { get; set; }
    public required string Tranche { get; set; }
    public decimal AmountUsd { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A document's hash and version (PLT-DOC-01). The file itself is never sent to or kept by the server in this phase.</summary>
public sealed class LendingDocument
{
    public Guid Id { get; set; }
    public Guid DealId { get; set; }
    public required string Name { get; set; }
    public required string Kind { get; set; }
    public int Version { get; set; }
    public required string Sha256 { get; set; }
    public Guid UploadedBy { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>A statement by the appointed attestor, carrying its report's hash (PLT-ORC-01).</summary>
public sealed class LendingAttestation
{
    public Guid Id { get; set; }
    public Guid DealId { get; set; }
    public required string Kind { get; set; }
    public Guid AttestorId { get; set; }
    public required string ReportSha256 { get; set; }
    public decimal? ValueUsd { get; set; }
    public int? DrawNumber { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>Cash the servicer pushed through the waterfall, and what each step paid: the reconciliation record of PLT-PAY-02.</summary>
public sealed class LendingPayment
{
    public Guid Id { get; set; }
    public Guid DealId { get; set; }
    public required string Reference { get; set; }
    public decimal AmountUsd { get; set; }
    public decimal Fees { get; set; }
    public decimal SeniorInterest { get; set; }
    public decimal SeniorPrincipal { get; set; }
    public decimal JuniorInterest { get; set; }
    public decimal SeniorPrincipalSweep { get; set; }
    public decimal JuniorPrincipal { get; set; }
    public decimal Reserve { get; set; }
    public decimal Residual { get; set; }
    public Guid RecordedBy { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>One half of the 2-of-2 markDefault (SC-DEF-01).</summary>
public sealed class LendingDefaultVote
{
    public Guid DealId { get; set; }
    public required string Role { get; set; }
    public Guid UserId { get; set; }
    public required string Reason { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>What Compliance has recorded about a person or entity (PLT-ID-04, PLT-CMP-02).</summary>
public sealed class LendingProfile
{
    public Guid UserId { get; set; }
    public required string InvestorClass { get; set; }
    public required string Jurisdiction { get; set; }
    public bool KybApproved { get; set; }
    public bool Frozen { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>PLT-AUD-01: a privileged action. A database trigger refuses updates and deletes, so it can't be rewritten.</summary>
public sealed class LendingAuditEvent
{
    public long Id { get; set; }
    public Guid? DealId { get; set; }
    public Guid ActorId { get; set; }
    public required string Action { get; set; }
    public string? Before { get; set; }
    public string? After { get; set; }
    public string? Reason { get; set; }
    public DateTimeOffset At { get; set; }
}

/// <summary>LND-FR-07, SC-VLT-01: a request to take money out, redeemable in a later window.</summary>
public sealed class LendingRedemption
{
    public Guid Id { get; set; }
    public Guid DealId { get; set; }
    public Guid UserId { get; set; }
    public required string Tranche { get; set; }
    public decimal AmountUsd { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset WindowOpensAt { get; set; }
    public DateTimeOffset WindowClosesAt { get; set; }
    public DateTimeOffset? RedeemedAt { get; set; }
}

/// <summary>PLT-NTF-01: an in-app notice of a change that affects someone.</summary>
public sealed class LendingNotice
{
    public long Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? DealId { get; set; }
    public required string Text { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
