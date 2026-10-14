using System.Numerics;
using System.Text;

namespace LaLira.Core.Ledger;

// IBANs as people type them ("ch93 0076 2011 6238 5295 7"): stored compact in upper case,
// checked with the ISO 13616 check digits and the country's length, shown in groups of four.
public static class Iban
{
    // Lengths per country (SWIFT IBAN registry); a country not listed only gets the checksum test.
    private static readonly Dictionary<string, int> Lengths = new()
    {
        ["AD"] = 24, ["AE"] = 23, ["AL"] = 28, ["AT"] = 20, ["AZ"] = 28, ["BA"] = 20, ["BE"] = 16, ["BG"] = 22, ["BH"] = 22,
        ["BR"] = 29, ["BY"] = 28, ["CH"] = 21, ["CR"] = 22, ["CY"] = 28, ["CZ"] = 24, ["DE"] = 22, ["DK"] = 18, ["DO"] = 28,
        ["EE"] = 20, ["EG"] = 29, ["ES"] = 24, ["FI"] = 18, ["FO"] = 18, ["FR"] = 27, ["GB"] = 22, ["GE"] = 22, ["GI"] = 23,
        ["GL"] = 18, ["GR"] = 27, ["GT"] = 28, ["HR"] = 21, ["HU"] = 28, ["IE"] = 22, ["IL"] = 23, ["IQ"] = 23, ["IS"] = 26,
        ["IT"] = 27, ["JO"] = 30, ["KW"] = 30, ["KZ"] = 20, ["LB"] = 28, ["LC"] = 32, ["LI"] = 21, ["LT"] = 20, ["LU"] = 20,
        ["LV"] = 21, ["MC"] = 27, ["MD"] = 24, ["ME"] = 22, ["MK"] = 19, ["MR"] = 27, ["MT"] = 31, ["MU"] = 30, ["NL"] = 18,
        ["NO"] = 15, ["PK"] = 24, ["PL"] = 28, ["PS"] = 29, ["PT"] = 25, ["QA"] = 29, ["RO"] = 24, ["RS"] = 22, ["SA"] = 24,
        ["SC"] = 31, ["SE"] = 24, ["SI"] = 19, ["SK"] = 24, ["SM"] = 27, ["TL"] = 23, ["TN"] = 24, ["TR"] = 26, ["UA"] = 29,
        ["VA"] = 22, ["VG"] = 24, ["XK"] = 20,
    };

    // Without spaces or dashes, upper case.
    public static string Normalize(string? text) =>
        new((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsValid(string compact)
    {
        if (compact.Length is < 15 or > 34 || !compact.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
            return false;
        if (!char.IsAsciiLetter(compact[0]) || !char.IsAsciiLetter(compact[1]) || !char.IsAsciiDigit(compact[2]) || !char.IsAsciiDigit(compact[3]))
            return false;
        if (Lengths.TryGetValue(compact[..2], out var length) && compact.Length != length)
            return false;

        // Move the first four characters to the end, letters become 10..35, the number mod 97 must be 1.
        var digits = new StringBuilder();
        foreach (var c in compact[4..] + compact[..4])
            digits.Append(char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString());
        return BigInteger.Parse(digits.ToString()) % 97 == 1;
    }

    // "CH93 0076 2011 6238 5295 7"
    public static string Format(string compact) =>
        string.Join(' ', Enumerable.Range(0, (compact.Length + 3) / 4).Select(i => compact.Substring(i * 4, Math.Min(4, compact.Length - i * 4))));
}
