using System.Security.Cryptography;
using System.Text;
using NdeipiChat.SubApps.Farm.Models;

namespace NdeipiChat.SubApps.Farm.Services;

/// <summary>
/// Local hash-chained ledger that mirrors what would be anchored on the Sousveillance Blockchain.
/// Each block commits to the previous block's hash, so any edit to history breaks the chain.
/// </summary>
public static class SousveillanceLedger
{
    private static readonly string GenesisHash = new('0', 64);

    public static LedgerBlock Append(FarmState state, string type, string payload)
    {
        var block = new LedgerBlock
        {
            Height = state.Ledger.Count,
            Day = state.Day,
            Type = type,
            Payload = payload,
            PrevHash = state.Ledger.Count == 0 ? GenesisHash : state.Ledger[^1].Hash,
        };
        block.Hash = ComputeHash(block);
        state.Ledger.Add(block);
        return block;
    }

    public static string ComputeHash(LedgerBlock b)
    {
        var raw = $"{b.Height}|{b.Day}|{b.Type}|{b.Payload}|{b.PrevHash}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    public static bool Validate(IReadOnlyList<LedgerBlock> chain)
    {
        for (var i = 0; i < chain.Count; i++)
        {
            var expectedPrev = i == 0 ? GenesisHash : chain[i - 1].Hash;
            if (chain[i].PrevHash != expectedPrev || chain[i].Hash != ComputeHash(chain[i])) return false;
        }
        return true;
    }
}
