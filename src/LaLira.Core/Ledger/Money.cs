using System.Globalization;

namespace LaLira.Core.Ledger;

// Amounts are stored as whole hundredths ("cents") in a long, never as floating point, so sums
// are exact. Formatting uses the person's region format.
public static class Money
{
    public const long Scale = 100;

    // The largest amount accepted from input: 1 trillion, well inside long range even when summed.
    public const long MaxAbsolute = 1_000_000_000_000L * Scale;

    public static decimal ToDecimal(long cents) => cents / (decimal)Scale;

    public static long FromDecimal(decimal value) => (long)Math.Round(value * Scale, MidpointRounding.AwayFromZero);

    // "31'833.00", or "31'833" without decimals (rounded for display only). Negative amounts get a
    // real minus sign, which lines up better in columns than a hyphen.
    public static string Format(long cents, CultureInfo culture, bool decimals = true, bool sign = false)
    {
        var text = Math.Abs(ToDecimal(cents)).ToString(decimals ? "N2" : "N0", culture);
        // Rounding without decimals can turn -0.40 into "0": no sign then.
        var isZero = text.All(c => !char.IsAsciiDigit(c) || c == '0');
        if (cents < 0 && !isZero)
            return "−" + text;
        if (sign && cents > 0 && !isZero)
            return "+" + text;
        return text;
    }

    // The plain number for an input field: no group separators, the culture's decimal mark.
    public static string FormatForInput(long cents, CultureInfo culture) =>
        ToDecimal(cents).ToString("0.00", culture);

    // Reads what a person typed or a CSV contains: "1'234.50", "1’234,50", "1.234,50", "-250",
    // "CHF 12.40", "12,5". Group separators (' ’ space and the one that isn't the decimal mark)
    // are ignored. With only one kind of separator, it's the decimal mark when one or two digits
    // follow its last occurrence, otherwise a group separator. More than two decimals is an error.
    public static bool TryParse(string? input, out long cents)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;

        var s = input.Trim();
        var negative = false;
        var sawMinus = false;
        if (s.StartsWith('(') && s.EndsWith(')'))
        {
            negative = true;
            s = s[1..^1];
        }

        var digits = new System.Text.StringBuilder();
        var separators = new List<(char C, int DigitsBefore)>();
        foreach (var c in s)
        {
            if (char.IsAsciiDigit(c))
                digits.Append(c);
            else if (c is '.' or ',')
                separators.Add((c, digits.Length));
            else if (c is '-' or '−')
            {
                // One minus, before or after the number ("250-" is common in bank exports).
                if (sawMinus)
                    return false;
                sawMinus = true;
                negative = !negative;
            }
            else if (c is '+' or '\'' or '’' or ' ' or ' ' or ' ' || char.IsLetter(c) || c is '$' or '€' or '£' or '¥')
                continue;
            else
                return false;
        }
        if (digits.Length == 0 || digits.Length > 18)
            return false;

        var decimalAt = -1; // number of digits before the decimal mark
        if (separators.Count > 0)
        {
            var last = separators[^1];
            var after = digits.Length - last.DigitsBefore;
            var kinds = separators.Select(x => x.C).Distinct().Count();
            if (kinds == 2)
            {
                // "1.234,50": the last separator is the decimal mark; the other kind must come before it only.
                if (separators.Take(separators.Count - 1).Any(x => x.C == last.C))
                    return false;
                decimalAt = last.DigitsBefore;
            }
            else if (separators.Count == 1 && after is 1 or 2)
                decimalAt = last.DigitsBefore;
            else if (separators.Count == 1 && after == 0)
                decimalAt = last.DigitsBefore; // "12." means 12
            else if (separators.Count > 1 && after is 1 or 2)
                return false; // "1.234.5" is ambiguous
        }

        // Group separators must group by thousands ("1.234.567"), so "0.125" or "12.3456" isn't
        // silently read as a whole number.
        var groups = separators.Where(x => x.DigitsBefore != decimalAt || decimalAt < 0).Select(x => x.DigitsBefore).ToList();
        if (decimalAt >= 0 && separators.Count(x => x.DigitsBefore == decimalAt) > 1)
            return false;
        if (groups.Count > 0)
        {
            var end = decimalAt < 0 ? digits.Length : decimalAt;
            if (groups[0] is < 1 or > 3 || end - groups[^1] != 3 || digits[0] == '0')
                return false;
            for (var i = 1; i < groups.Count; i++)
                if (groups[i] - groups[i - 1] != 3)
                    return false;
        }

        var whole = decimalAt < 0 ? digits.ToString() : digits.ToString(0, decimalAt);
        var fraction = decimalAt < 0 ? "" : digits.ToString(decimalAt, digits.Length - decimalAt);
        if (fraction.Length > 2)
            return false;

        var value = (whole.Length == 0 ? 0 : long.Parse(whole, CultureInfo.InvariantCulture)) * Scale
                    + (fraction.Length == 0 ? 0 : long.Parse(fraction.PadRight(2, '0'), CultureInfo.InvariantCulture));
        if (value > MaxAbsolute)
            return false;
        cents = negative ? -value : value;
        return true;
    }
}
