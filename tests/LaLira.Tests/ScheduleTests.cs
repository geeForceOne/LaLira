using System.Globalization;
using LaLira.Core.Ledger;
using LaLira.Core.Models;

namespace LaLira.Tests;

public class ScheduleTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void MonthlyOnTheFirst()
    {
        var s = new RuleSchedule(Frequency.Monthly, 1, 1, D(2009, 5, 1), null);
        Assert.Equal([D(2009, 5, 1), D(2009, 6, 1), D(2009, 7, 1)], s.Between(D(2009, 5, 1), D(2009, 7, 31)));
    }

    [Fact]
    public void StartAfterTheDayBeginsNextMonth()
    {
        var s = new RuleSchedule(Frequency.Monthly, 1, 1, D(2026, 10, 14), null);
        Assert.Equal(D(2026, 11, 1), s.NextOnOrAfter(D(2026, 10, 14)));
    }

    [Fact]
    public void LastDayFollowsMonthLength()
    {
        var s = new RuleSchedule(Frequency.Monthly, 1, RuleSchedule.LastDay, D(2024, 1, 1), null);
        Assert.Equal([D(2024, 1, 31), D(2024, 2, 29), D(2024, 3, 31), D(2024, 4, 30)], s.Between(D(2024, 1, 1), D(2024, 4, 30)));
    }

    [Fact]
    public void QuarterlyAndEveryTwoMonths()
    {
        var q = new RuleSchedule(Frequency.Quarterly, 1, 15, D(2026, 1, 1), null);
        Assert.Equal([D(2026, 1, 15), D(2026, 4, 15), D(2026, 7, 15), D(2026, 10, 15)], q.Between(D(2026, 1, 1), D(2026, 12, 31)));
        var two = new RuleSchedule(Frequency.Monthly, 2, 1, D(2026, 1, 1), null);
        Assert.Equal([D(2026, 1, 1), D(2026, 3, 1), D(2026, 5, 1)], two.Between(D(2026, 1, 1), D(2026, 6, 30)));
    }

    [Fact]
    public void WeeklyFromTheStartDate()
    {
        var s = new RuleSchedule(Frequency.Weekly, 1, 5, D(2026, 10, 5), null);
        Assert.Equal([D(2026, 10, 5), D(2026, 10, 12), D(2026, 10, 19)], s.Between(D(2026, 10, 1), D(2026, 10, 20)));
        Assert.Equal(D(2026, 10, 12), s.NextAfter(D(2026, 10, 5)));
    }

    [Fact]
    public void YearlyOnTheLastDayOfFebruary()
    {
        var s = new RuleSchedule(Frequency.Yearly, 1, 29, D(2024, 2, 1), null);
        Assert.Equal([D(2024, 2, 29), D(2025, 2, 28), D(2026, 2, 28)], s.Between(D(2024, 1, 1), D(2026, 12, 31)));
    }

    [Fact]
    public void EndDateStops()
    {
        var s = new RuleSchedule(Frequency.Monthly, 1, 1, D(2026, 1, 1), D(2026, 3, 1));
        Assert.Equal(3, s.Between(D(2026, 1, 1), D(2027, 1, 1)).Count());
        Assert.Null(s.NextAfter(D(2026, 3, 1)));
    }

    [Theory]
    [InlineData("Einzahlung {month}", "de", "Einzahlung Oktober")]
    [InlineData("Deposit {month}", "en", "Deposit October")]
    [InlineData("Zinsen {prevmonth} {year}", "de", "Zinsen September 2026")]
    [InlineData("{quarter}/{year}: {monthshort}", "de", "Q4/2026: Okt")]
    [InlineData("Unknown {foo} stays", "en", "Unknown {foo} stays")]
    [InlineData("{MONTH}", "de", "Oktober")]
    public void Placeholders(string template, string language, string expected) =>
        Assert.Equal(expected, DescriptionTemplate.Render(template, D(2026, 10, 1), language, CultureInfo.GetCultureInfo("de-CH")));

    [Fact]
    public void DateUsesRegionFormat() =>
        Assert.Equal("Fee 01.10.2026", DescriptionTemplate.Render("Fee {date}", D(2026, 10, 1), "en", CultureInfo.GetCultureInfo("de-CH")));
}
