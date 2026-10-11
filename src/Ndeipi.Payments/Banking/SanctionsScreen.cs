using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ndeipi.Payments.Api;

namespace Ndeipi.Payments.Banking;

/// <summary>
/// Screens payees against OFAC's Specially Designated Nationals list before money goes to them
/// (NFR-CMP-01): beneficiary names, and wallet addresses for crypto payouts.
///
/// Names match when every word of a listed name appears in the payee's name, ignoring case,
/// punctuation and word order ("SMITH, John" matches "John A Smith"). That errs towards stopping a
/// payment for a person to look at; it is not a substitute for a compliance vendor's fuzzy matching.
/// </summary>
public sealed partial class SanctionsScreen
{
    readonly List<string[]> _names = [];
    readonly HashSet<string> _addresses = new(StringComparer.OrdinalIgnoreCase);

    public SanctionsScreen(IOptions<PaymentsOptions> options, ILogger<SanctionsScreen> log)
    {
        var sanctions = options.Value.Banking.Sanctions;
        if (sanctions.SdnCsvPath is { Length: > 0 } path)
        {
            foreach (var row in ReadCsv(File.ReadAllText(path)))
            {
                if (row.Count < 2)
                    continue;
                AddName(row[1]);
                if (row.Count >= 12)
                    foreach (Match m in DigitalCurrencyAddress().Matches(row[11]))
                        _addresses.Add(m.Groups[1].Value);
            }
            log.LogInformation("Loaded {Names} names and {Addresses} addresses from the SDN list", _names.Count, _addresses.Count);
        }
        foreach (var name in sanctions.Names)
            AddName(name);
        foreach (var address in sanctions.Addresses)
            _addresses.Add(address.Trim());
        IsConfigured = sanctions.SdnCsvPath is { Length: > 0 };
    }

    /// <summary>Whether the SDN list itself is loaded; production banking refuses to start without it.</summary>
    public bool IsConfigured { get; }

    /// <summary>Why the payee may not be paid, or null if it may.</summary>
    public string? Check(string name, string? address)
    {
        if (address is { Length: > 0 } && _addresses.Contains(address.Trim()))
            return "The wallet address is on OFAC's sanctions list.";
        var words = Words(name).ToHashSet();
        foreach (var listed in _names)
            if (listed.All(words.Contains))
                return $"The name matches \"{string.Join(' ', listed)}\" on OFAC's sanctions list.";
        return null;
    }

    void AddName(string name)
    {
        var words = Words(name).ToArray();
        // A one-word entry would stop everyone who shares a common word with it; those are vessels
        // and aircraft identifiers more than people, and the address check covers wallets.
        if (words.Length >= 2 || words is [{ Length: >= 6 }])
            _names.Add(words);
    }

    static IEnumerable<string> Words(string name)
    {
        var plain = new StringBuilder(name.Length);
        foreach (var c in name.Normalize(NormalizationForm.FormD))
        {
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark)
                continue;
            plain.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : ' ');
        }
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w is not ("THE" or "OF" or "AND"));
    }

    /// <summary>RFC 4180 rows: quoted fields may hold commas, quotes ("") and line breaks.</summary>
    static IEnumerable<List<string>> ReadCsv(string text)
    {
        var row = new List<string>();
        var field = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                    quoted = false;
                else
                    field.Append(c);
            }
            else if (c == '"')
                quoted = true;
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
            }
            else if (c is '\n' or '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                    i++;
                row.Add(field.ToString());
                field.Clear();
                yield return row;
                row = [];
            }
            else
                field.Append(c);
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            yield return row;
        }
    }

    // "Digital Currency Address - XBT 1A2b...;" in the SDN remarks.
    [GeneratedRegex(@"Digital Currency Address - [A-Z0-9]+ ([A-Za-z0-9]+)")]
    private static partial Regex DigitalCurrencyAddress();
}
