using LaLira.Core.Models;

namespace LaLira.Core.Reports;

public readonly record struct DateRange(DateOnly Start, DateOnly End);

public static class ReportPeriods
{
    // Reports go out at this hour (the owner's time) on the first day after their period, after
    // the recurring bookings of that night.
    public static readonly TimeOnly SendTime = new(7, 0);

    // The period that contains `date`.
    public static DateRange Containing(ReportPeriod period, DateOnly date)
    {
        var start = period switch
        {
            ReportPeriod.Quarterly => new DateOnly(date.Year, (date.Month - 1) / 3 * 3 + 1, 1),
            ReportPeriod.Yearly => new DateOnly(date.Year, 1, 1),
            _ => new DateOnly(date.Year, date.Month, 1),
        };
        var months = period switch { ReportPeriod.Quarterly => 3, ReportPeriod.Yearly => 12, _ => 1 };
        return new DateRange(start, start.AddMonths(months).AddDays(-1));
    }

    public static DateRange After(ReportPeriod period, DateOnly previousEnd) => Containing(period, previousEnd.AddDays(1));

    public static DateRange Before(ReportPeriod period, DateOnly date) => Containing(period, Containing(period, date).Start.AddDays(-1));

    // When the report for `range` goes out, in the owner's local time.
    public static DateTime SendAt(DateRange range) => range.End.AddDays(1).ToDateTime(SendTime);
}
