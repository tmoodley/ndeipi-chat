using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Ndeipi.Payments.Api;

/// <summary>
/// Public object IDs: a type prefix and a ULID (<c>usr_01JA8Z2F5G6H7J8K9M0N1P2Q3R</c>). ULIDs sort
/// by creation time, which is what cursor pagination orders by (FR-API-04).
/// </summary>
public static class Ids
{
    public const string User = "usr";
    public const string Wallet = "wal";
    public const string Transfer = "trf";
    public const string DepositAccount = "da";
    public const string PayoutAccount = "pa";
    public const string WebhookEndpoint = "we";
    public const string Event = "evt";
    public const string Request = "req";
    public const string Posting = "pst";
    public const string Account = "acct";

    const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public const string WalletHistoryEntry = "whe";

    public static string New(string prefix, TimeProvider clock) => $"{prefix}_{Ulid(clock.GetUtcNow())}";

    /// <summary>
    /// A public ID for a row numbered by the database (a ledger line), in the same 26-character
    /// shape as a ULID, so it sorts and pages like every other ID.
    /// </summary>
    public static string FromNumber(string prefix, long number)
    {
        Span<char> chars = stackalloc char[26];
        var value = (ulong)number;
        for (var i = 25; i >= 0; i--, value >>= 5)
            chars[i] = Crockford[(int)(value & 31)];
        return $"{prefix}_{new string(chars)}";
    }

    /// <summary>The number in an ID from <see cref="FromNumber"/>, or null if it is not one.</summary>
    public static long? ToNumber(string prefix, string id)
    {
        if (!id.StartsWith(prefix + "_", StringComparison.Ordinal) || id.Length != prefix.Length + 27)
            return null;
        var digits = id.AsSpan(prefix.Length + 1);

        // A long fits in the last 13 characters, the first of which carries only 3 bits.
        if (digits[..13].ContainsAnyExcept('0') || Crockford.IndexOf(digits[13]) is < 0 or > 7)
            return null;
        ulong value = 0;
        foreach (var c in digits[13..])
        {
            var digit = Crockford.IndexOf(c);
            if (digit < 0)
                return null;
            value = (value << 5) | (uint)digit;
        }
        return (long)value;
    }

    /// <summary>26 Crockford base-32 characters: 48 bits of milliseconds, then 80 random bits.</summary>
    public static string Ulid(DateTimeOffset at)
    {
        Span<byte> bytes = stackalloc byte[16];
        var ms = (ulong)at.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--, ms >>= 8)
            bytes[i] = (byte)ms;
        RandomNumberGenerator.Fill(bytes[6..]);

        // 128 bits as 26 five-bit groups; the first group holds only the top 3 bits.
        var value = new UInt128(BinaryPrimitives.ReadUInt64BigEndian(bytes[..8]), BinaryPrimitives.ReadUInt64BigEndian(bytes[8..]));
        Span<char> chars = stackalloc char[26];
        for (var i = 25; i >= 0; i--, value >>= 5)
            chars[i] = Crockford[(int)(value & 31)];
        return new string(chars);
    }
}
