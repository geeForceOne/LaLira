using LaLira.Core.Ledger;
using LaLira.Core.Models;
using LaLira.Core.Reports;
using LaLira.Core.Services;

namespace LaLira.Tests;

public class LedgerTests
{
    private static DateOnly D(int y, int m, int d) => new(y, m, d);

    [Fact]
    public void RunningBalancesAndYearEnds()
    {
        // The user's example: 250 a month, a correction of 569 on 1.1.2010.
        var positions = new List<LedgerPosition>();
        var id = 1;
        for (var m = 5; m <= 12; m++) positions.Add(new(id++, D(2009, m, 1), 25000, $"Einzahlung {m}", null));
        positions.Add(new(id++, D(2010, 1, 1), 25000, "Einzahlung Januar", null));
        positions.Add(new(id++, D(2010, 1, 1), 56900, "Korrekturbuchung und Zinsen", null));
        positions.Add(new(id++, D(2010, 2, 1), 25000, "Einzahlung Februar", null));

        var result = LedgerBuilder.Build(3158300, D(2009, 4, 30), positions, D(2010, 2, 15), "Opening balance");

        Assert.Equal(3465200, result.BalanceToday);
        Assert.Equal([2010, 2009], result.Years.Select(y => y.Year));
        var y2009 = result.Years[1];
        Assert.True(y2009.IsPast);
        Assert.Equal(3358300, y2009.EndBalance); // 33'583 on 31.12.2009, as in the example
        Assert.Equal(200000, y2009.In);
        var y2010 = result.Years[0];
        Assert.False(y2010.IsPast);
        // Newest first; same-day positions keep their order (Januar before the correction).
        Assert.Equal("Einzahlung Februar", y2010.Rows[0].Description);
        Assert.Equal(3440200, y2010.Rows[1].Balance);
        Assert.Equal(3383300, y2010.Rows[2].Balance);
    }

    [Fact]
    public void UpcomingIsListedButNotInToday()
    {
        var result = LedgerBuilder.Build(10000, D(2026, 1, 1),
            [new(1, D(2026, 10, 1), 500, "now", null), new(2, D(2026, 12, 1), 700, "later", null)], D(2026, 10, 14), "Opening");
        Assert.Equal(10500, result.BalanceToday);
        Assert.Equal(11200, result.BalanceWithUpcoming);
        Assert.Equal(1, result.UpcomingCount);
        Assert.True(result.Years[0].Rows[0].IsUpcoming);
    }

    [Fact]
    public void ReportPeriodsAndSendTimes()
    {
        Assert.Equal(new DateRange(D(2026, 10, 1), D(2026, 10, 31)), ReportPeriods.Containing(ReportPeriod.Monthly, D(2026, 10, 14)));
        Assert.Equal(new DateRange(D(2026, 7, 1), D(2026, 9, 30)), ReportPeriods.Before(ReportPeriod.Quarterly, D(2026, 10, 14)));
        Assert.Equal(new DateTime(2026, 11, 1, 7, 0, 0), ReportPeriods.SendAt(ReportPeriods.Containing(ReportPeriod.Monthly, D(2026, 10, 3))));
    }

    [Fact]
    public void PendingReportsCatchUp()
    {
        var report = new EmailReport { Period = ReportPeriod.Monthly, LastPeriodEnd = D(2026, 6, 30) };
        // 1 October 07:30: July, August and September are due.
        var pending = ReportService.Pending(report, new DateTime(2026, 10, 1, 7, 30, 0));
        Assert.Equal([D(2026, 7, 31), D(2026, 8, 31), D(2026, 9, 30)], pending.Select(p => p.End));
        // Before 07:00 on the 1st, September isn't due yet.
        Assert.Equal(2, ReportService.Pending(report, new DateTime(2026, 10, 1, 6, 59, 0)).Count);
    }
}
