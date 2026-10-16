namespace LaLira.Core.Ledger;

// ISINs as people type them ("ch 0038863350"): stored compact in upper case and checked with
// the ISO 6166 check digit (letters become 10..35, then the Luhn test over all digits).
public static class Isin
{
    public static string Normalize(string? text) =>
        new((text ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    public static bool IsValid(string compact)
    {
        if (compact.Length != 12 || !char.IsAsciiLetterUpper(compact[0]) || !char.IsAsciiLetterUpper(compact[1]) || !char.IsAsciiDigit(compact[11]))
            return false;
        if (!compact.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c)))
            return false;

        var digits = string.Concat(compact.Select(c => char.IsAsciiDigit(c) ? c.ToString() : (c - 'A' + 10).ToString()));
        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i] - '0';
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9) d -= 9;
            }
            sum += d;
        }
        return sum % 10 == 0;
    }

    // Swiss and Liechtenstein ISINs contain the Valor: the nine digits after the country code
    // without leading zeros (CH0244767585 is Valor 24476758). Null for other ISINs.
    public static string? Valor(string? isin)
    {
        var compact = Normalize(isin);
        if (!IsValid(compact) || compact[..2] is not ("CH" or "LI") || !compact[2..11].All(char.IsAsciiDigit))
            return null;
        var valor = compact[2..11].TrimStart('0');
        return valor.Length == 0 ? null : valor;
    }
}
