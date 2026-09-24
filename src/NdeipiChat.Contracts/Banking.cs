namespace NdeipiChat.Contracts;

/// <param name="KycStatus">Bridge KYC status ("not_started", "under_review", "approved", ...), or "none" before a KYC link exists.</param>
/// <param name="TosStatus">Bridge terms-of-service status ("pending" or "approved"), or "none".</param>
/// <param name="CanTransfer">KYC approved, terms accepted and a Bridge wallet created.</param>
/// <param name="Currency">The stablecoin users send each other on this server, e.g. "usdc".</param>
public sealed record BankingStatusDto(
    string KycStatus,
    string TosStatus,
    bool CanTransfer,
    string? WalletChain,
    string? WalletAddress,
    string Currency);

/// <summary>Where to send the user: accept Bridge's terms first, then complete KYC.</summary>
public sealed record KycLinkDto(string KycUrl, string TosUrl, BankingStatusDto Status);

public sealed record BalanceDto(string Currency, string Amount, string Chain);
