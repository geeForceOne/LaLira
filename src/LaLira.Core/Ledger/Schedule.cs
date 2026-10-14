using LaLira.Core.Models;

namespace LaLira.Core.Ledger;

// When a recurring rule books. Weekly rules repeat from the start date; monthly, quarterly and
// yearly ones on DayOfMonth, counted in months from the start date's month. A day the month
// doesn't have (31 in April, 29-31 in February) falls on the month's last day.
public readonly record struct RuleSchedule(Frequency Frequency, int Interval, int DayOfMonth, DateOnly Start, DateOnly? End)
{
    public const int LastDay = 31;

    public static RuleSchedule Of(RecurringRule r) => new(r.Frequency, r.Interval, r.DayOfMonth, r.StartDate, r.EndDate);

    private int StepMonths => Frequency switch
    {
        Frequency.Quarterly => 3 * Interval,
        Frequency.Yearly => 12 * Interval,
        _ => Interval,
    };

    // The first booking date on or after `from` (never before the start), or null after the end.
    public DateOnly? NextOnOrAfter(DateOnly from)
    {
        if (from < Start)
            from = Start;

        DateOnly date;
        if (Frequency == Frequency.Weekly)
        {
            var step = 7 * Interval;
            var k = (from.DayNumber - Start.DayNumber + step - 1) / step;
            date = Start.AddDays(k * step);
        }
        else
        {
            var startMonth = Start.Year * 12 + Start.Month - 1;
            var fromMonth = from.Year * 12 + from.Month - 1;
            var k = Math.Max(0, (fromMonth - startMonth) / StepMonths);
            date = InMonth(startMonth + k * StepMonths);
            while (date < from)
                date = InMonth(startMonth + ++k * StepMonths);
        }
        return End is { } end && date > end ? null : date;
    }

    // The booking date after `date`.
    public DateOnly? NextAfter(DateOnly date) => NextOnOrAfter(date.AddDays(1));

    // Every booking date from `from` to `to`, both included.
    public IEnumerable<DateOnly> Between(DateOnly from, DateOnly to)
    {
        for (var d = NextOnOrAfter(from); d is { } day && day <= to; d = NextAfter(day))
            yield return day;
    }

    private DateOnly InMonth(int monthIndex)
    {
        var year = monthIndex / 12;
        var month = monthIndex % 12 + 1;
        return new DateOnly(year, month, Math.Min(DayOfMonth, DateTime.DaysInMonth(year, month)));
    }
}
