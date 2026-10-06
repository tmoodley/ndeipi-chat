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

    public static string New(string prefix, TimeProvider clock) => $"{prefix}_{Ulid(clock.GetUtcNow())}";

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
