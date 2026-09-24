namespace NdeipiChat.Contracts;

public sealed record TextPayload(string Text);

/// <summary>
/// Identifies any token on any chain. Fungible tokens are sent by amount; non-fungible ones
/// (ERC-721, ERC-1155) name the <see cref="TokenId"/> being sent.
/// </summary>
/// <param name="Chain">Chain the token lives on, as Ndeipi Enterprise Server names it -- e.g. "polygon".</param>
/// <param name="Symbol">Display symbol, e.g. "NMX" or "AGD".</param>
/// <param name="Standard">One of <see cref="TokenStandards"/>.</param>
/// <param name="ContractAddress">Contract or mint address; null for the chain's native coin.</param>
/// <param name="Decimals">Decimal places of a fungible token, used to reject over-precise amounts.</param>
/// <param name="TokenId">NFT id, as a decimal string (uint256 does not fit a long).</param>
public sealed record TokenRef(
    string Chain,
    string Symbol,
    string Standard,
    string? ContractAddress,
    int? Decimals,
    string? TokenId);

public static class TokenStandards
{
    public const string Native = "native";
    public const string Erc20 = "erc20";
    public const string Erc721 = "erc721";
    public const string Erc1155 = "erc1155";
    public const string Spl = "spl";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Native, Erc20, Erc721, Erc1155, Spl, Other];

    public static bool IsNonFungible(string standard) => standard is Erc721 or Erc1155;
}

/// <param name="Amount">Decimal string in invariant culture ("12.5"); "1" for an ERC-721.</param>
public sealed record AssetTransferPayload(Guid RecipientId, TokenRef Token, string Amount, string? Memo);

/// <summary>Status of a token transfer, written back by Ndeipi Enterprise Server.</summary>
public sealed record AssetTransferState(Guid TransferId, string Status, string? TxHash, string? Error);

public static class TransferStatuses
{
    public const string Pending = "Pending";
    public const string Processing = "Processing";
    public const string Confirmed = "Confirmed";
    public const string Failed = "Failed";

    public static bool IsFinal(string status) => status is Confirmed or Failed;
}

/// <param name="Amount">Decimal string in invariant culture, in <paramref name="Currency"/>.</param>
public sealed record BankTransferPayload(Guid RecipientId, string Amount, string Currency, string? Memo);

/// <param name="Status">One of <see cref="TransferStatuses"/>.</param>
/// <param name="ProviderState">Bridge's own transfer state, e.g. "payment_processed".</param>
public sealed record BankTransferState(Guid TransferId, string Status, string? ProviderState, string? Error);
