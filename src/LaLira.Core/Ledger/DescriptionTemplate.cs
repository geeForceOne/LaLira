using System.Globalization;
using System.Text.RegularExpressions;
using LaLira.Core.Localization;

namespace LaLira.Core.Ledger;

// Placeholders in a recurring rule's description, filled in with the booking date:
// "Einzahlung {month}" on 1.10.2026 in German becomes "Einzahlung Oktober".
public static partial class DescriptionTemplate
{
    // For the help text in the rule dialog: placeholder and what it means (English, translated there).
    public static readonly IReadOnlyList<(string Placeholder, string Meaning)> Placeholders =
    [
        ("{month}", "month name"),
        ("{monthshort}", "short month name"),
        ("{prevmonth}", "previous month's name"),
        ("{year}", "year"),
        ("{prevyear}", "previous year"),
        ("{quarter}", "quarter, like Q4"),
        ("{date}", "booking date"),
    ];

    // language: for month names. format: the person's region format, for {date}.
    public static string Render(string template, DateOnly date, string language, CultureInfo format)
    {
        var previous = date.AddMonths(-1);
        return PlaceholderPattern().Replace(template, m => m.Groups[1].Value.ToLowerInvariant() switch
        {
            "month" => L.MonthName(language, date.Month),
            "monthshort" => L.MonthShortName(language, date.Month),
            "prevmonth" => L.MonthName(language, previous.Month),
            "year" => date.Year.ToString(CultureInfo.InvariantCulture),
            "prevyear" => (date.Year - 1).ToString(CultureInfo.InvariantCulture),
            "quarter" => "Q" + ((date.Month - 1) / 3 + 1),
            "date" => date.ToString("d", format),
            _ => m.Value,
        }).Trim();
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();
}
