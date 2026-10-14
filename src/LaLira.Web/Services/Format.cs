using System.Globalization;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Services;

namespace LaLira.Web.Services;

// Numbers and dates the way the person's region format writes them (the request's culture).
public static class Format
{
    private static CultureInfo C => CultureInfo.CurrentCulture;

    public static string Money(long cents, bool decimals = true, bool sign = false) => Core.Ledger.Money.Format(cents, C, decimals, sign);

    public static string Money(long cents, AccountInfo account, bool sign = false) => Money(cents, account.ShowDecimals, sign);

    public static string Input(long cents) => Core.Ledger.Money.FormatForInput(Math.Abs(cents), C);

    public static string Date(DateOnly date) => date.ToString("d", C);

    // "Tue, 14.10.2026"
    public static string DateWithDay(DateOnly date) => date.ToString("ddd", C).TrimEnd('.') + ", " + Date(date);

    public static string Date(DateTime utc, string timeZone) => Regions.LocalNow(utc, timeZone).ToString("d", C);

    public static string DateTime(DateTime utc, string timeZone) => Regions.LocalNow(utc, timeZone).ToString("g", C);

    public static string Time(TimeOnly time) => time.ToString("t", C);

    public static string Ago(Texts t, DateTime utc)
    {
        var span = System.DateTime.UtcNow - utc;
        if (span.TotalMinutes < 1) return t["just now"];
        if (span.TotalHours < 1) return t.Plural((int)span.TotalMinutes, "{0} minute ago", "{0} minutes ago");
        if (span.TotalDays < 1) return t.Plural((int)span.TotalHours, "{0} hour ago", "{0} hours ago");
        if (span.TotalDays < 60) return t.Plural((int)span.TotalDays, "{0} day ago", "{0} days ago");
        return utc.ToString("d", C);
    }

    public static string Size(long bytes) =>
        bytes >= 1024 * 1024 ? (bytes / 1024d / 1024d).ToString("0.0", C) + " MB" : Math.Max(1, bytes / 1024).ToString(C) + " KB";

    public static string Frequency(Texts t, Frequency f, int interval) => (f, interval) switch
    {
        (Core.Models.Frequency.Weekly, 1) => t["Every week"],
        (Core.Models.Frequency.Weekly, _) => t["Every {0} weeks", interval],
        (Core.Models.Frequency.Monthly, 1) => t["Every month"],
        (Core.Models.Frequency.Monthly, _) => t["Every {0} months", interval],
        (Core.Models.Frequency.Quarterly, 1) => t["Every quarter"],
        (Core.Models.Frequency.Quarterly, _) => t["Every {0} quarters", interval],
        (_, 1) => t["Every year"],
        _ => t["Every {0} years", interval],
    };

    // "on the 1st" / "on the last day" / "on Mondays"
    public static string When(Texts t, RuleView r) => r.Frequency == Core.Models.Frequency.Weekly
        ? t["on {0}", r.StartDate.ToString("dddd", C)]
        : r.DayOfMonth >= RuleSchedule.LastDay ? t["on the last day"] : t["on day {0}", r.DayOfMonth];

    public static string Period(Texts t, ReportPeriod p) => p switch
    {
        ReportPeriod.Quarterly => t["Quarterly"],
        ReportPeriod.Yearly => t["Yearly"],
        _ => t["Monthly"],
    };
}
