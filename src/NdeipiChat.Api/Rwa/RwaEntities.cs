namespace NdeipiChat.Api.Rwa;

/// <summary>An asset on sale (SRS rwa_assets). Phase 1 only has Green Island villas.</summary>
public sealed class RwaAsset
{
    public Guid Id { get; set; }

    /// <summary>"GI-VILLA-01".</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public required string Location { get; set; }
    public required string AssetClass { get; set; }
    public required string TokenStandard { get; set; }
    public required string Summary { get; set; }

    /// <summary>Lines shown on the villa's page, newline-separated.</summary>
    public string Highlights { get; set; } = "";

    public int Bedrooms { get; set; }

    /// <summary>The price of the whole villa for a year (the 1/1 tranche); weeks and months are priced from it.</summary>
    public decimal UnitPriceUsd { get; set; }

    /// <summary>The Year 1 vault's NAV when the pre-sale opened. AssetsPerShare is <see cref="CurrentNavUsd"/> over this.</summary>
    public decimal BaseNavUsd { get; set; }

    /// <summary>Changed only by an oracle (FR-2.4, NFR-2).</summary>
    public decimal CurrentNavUsd { get; set; }

    public decimal GoldWeightGramsPerUnit { get; set; }
    public required string ConstructionPhase { get; set; }

    /// <summary>The deployed contract. Null: none is deployed while settlement is simulated.</summary>
    public string? PolygonContractAddress { get; set; }

    public DateTimeOffset NavUpdatedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A base villa token someone bought (ERC-1155 mirror): a week, month or year of one villa, every year.</summary>
public sealed class RwaHolding
{
    public Guid Id { get; set; }

    /// <summary>The token's number, from an identity column, so each is unique without a race.</summary>
    public long TokenId { get; set; }

    public Guid UserId { get; set; }
    public Guid AssetId { get; set; }

    /// <summary>The buyer's request id, so a retried purchase returns the first one instead of buying twice.</summary>
    public Guid RequestId { get; set; }

    public required string IntervalType { get; set; }
    public int IntervalIndex { get; set; }

    /// <summary>The week's season for a weekly tranche; empty for a month or a year.</summary>
    public string Season { get; set; } = "";

    public decimal SeasonWeight { get; set; } = 1m;
    public decimal ShareFraction { get; set; }
    public decimal PrincipalUsd { get; set; }
    public required string PaymentMethod { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset PurchasedAt { get; set; }

    /// <summary>FR-1.3: until then the buyer can cancel for a full refund, and the money stays in escrow.</summary>
    public DateTimeOffset CoolingOffEndsAt { get; set; }

    public DateTimeOffset? CancelledAt { get; set; }
    public DateTimeOffset? VestedAt { get; set; }

    /// <summary>FR-2.2: Year 1 vault shares, the principal over AssetsPerShare when the cooling-off ended.</summary>
    public decimal VaultShares { get; set; }

    /// <summary>Null while simulated: nothing is minted.</summary>
    public string? PolygonTxHash { get; set; }
}

/// <summary>
/// One week of one villa's calendar, taken by a holding. Its key is (asset, week), so a week can't be
/// sold twice however many people buy it at once; a month or year takes all its weeks.
/// </summary>
public sealed class RwaWeekSlot
{
    public Guid AssetId { get; set; }
    public int Week { get; set; }
    public Guid HoldingId { get; set; }
}

/// <summary>A person's simulated US dollar balance. It stands in for money until live settlement is allowed.</summary>
public sealed class RwaSimAccount
{
    public Guid UserId { get; set; }
    public decimal BalanceUsd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>The audit and KYC log (SRS section 2): KYC checks, purchases, cancellations, vesting and NAV updates.</summary>
public sealed class RwaAuditEvent
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }

    /// <summary>"kyc_check", "mint", "cancel", "vest" or "nav".</summary>
    public required string Kind { get; set; }

    public Guid? UserId { get; set; }
    public Guid? AssetId { get; set; }
    public Guid? HoldingId { get; set; }
    public required string Detail { get; set; }
}
