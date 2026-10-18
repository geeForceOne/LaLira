using System.Globalization;

namespace LaLira.Core.Ledger;

// Quantities and prices per share, which can have more decimals than money ("12.345" shares at
// "87.1234"). Kept as decimal, never floating point.
public static class Decimals
{
    public const int MaxDecimals = 6;
    public const decimal MaxValue = 1_000_000_000_000m;

    // Reads "1'234.5", "1.234,5", "0,125" or "12.3456". With both '.' and ',' the last one is the
    // decimal mark. With one of them only, it's the decimal mark, unless it's the region's group
    // separator followed by exactly three digits ("1.234" in de-DE is 1234), or it appears more
    // than once. Negative numbers and more than six decimals aren't accepted.
    public static bool TryParse(string? input, CultureInfo culture, out decimal value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(input))
            return false;
        var s = new string(input.Where(c => c is not ('\'' or '’' or ' ' or ' ' or ' ')).ToArray());
        if (s.Length == 0 || s.Any(c => !char.IsAsciiDigit(c) && c is not ('.' or ',')))
            return false;

        var dots = s.Count(c => c == '.');
        var commas = s.Count(c => c == ',');
        char? mark = null;
        if (dots > 0 && commas > 0)
        {
            mark = s.LastIndexOf('.') > s.LastIndexOf(',') ? '.' : ',';
            if (s.Count(c => c == mark) > 1)
                return false;
        }
        else if (dots + commas == 1)
        {
            var sep = dots == 1 ? '.' : ',';
            var after = s.Length - s.IndexOf(sep) - 1;
            var group = culture.NumberFormat.NumberGroupSeparator;
            var isGroup = group == sep.ToString() && culture.NumberFormat.NumberDecimalSeparator != group && after == 3;
            mark = isGroup ? null : sep;
        }

        var whole = mark is { } m ? s[..s.IndexOf(m)] : s;
        var fraction = mark is { } m2 ? s[(s.IndexOf(m2) + 1)..] : "";
        whole = new string(whole.Where(char.IsAsciiDigit).ToArray());
        if (fraction.Any(c => !char.IsAsciiDigit(c)) || fraction.Length > MaxDecimals || whole.Length + fraction.Length == 0 || whole.Length > 13)
            return false;

        value = decimal.Parse((whole.Length == 0 ? "0" : whole) + (fraction.Length > 0 ? "." + fraction : ""), CultureInfo.InvariantCulture);
        return value <= MaxValue;
    }

    // "1'234.5": group separators, only the decimals it has (up to six).
    public static string Format(decimal value, CultureInfo culture) =>
        value.ToString("#,0.######", culture);

    // A price: at least two decimals, up to six ("87.10", "0.1234").
    public static string FormatPrice(decimal value, CultureInfo culture) =>
        value.ToString("#,0.00####", culture);

    // "+12.40%", "−3.10%"
    public static string FormatPercent(decimal value, CultureInfo culture) =>
        (value > 0 ? "+" : value < 0 ? "−" : "") + Math.Abs(value).ToString("0.00", culture) + "%";

    // For an input field: no group separators.
    public static string FormatForInput(decimal value, CultureInfo culture) =>
        value.ToString("0.######", culture);

    // Quantity times price, in cents (rounded half away from zero).
    public static long Total(decimal quantity, decimal price) => Money.FromDecimal(quantity * price);
}
