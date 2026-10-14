using System.Globalization;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Reports;

namespace LaLira.Tests;

public class ReportEmailTests
{
    [Fact]
    public void ReportShowsInAndOutButNoBalances()
    {
        var t = new Texts("de", CultureInfo.GetCultureInfo("de-CH"));
        var range = new DateRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var account = new ReportAccount("Sparkonto", "CHF", true, 13611000, 13697000, false,
            [new ReportLine(new DateOnly(2026, 9, 1), "September", 86000, 13697000)]);
        var email = ReportEmail.Build(t, "Anna", ReportPeriod.Monthly, range, [account], null);

        Assert.Contains("Eingang 860.00", email.TextBody);
        Assert.Contains("01.09.2026  September  860.00", email.TextBody);
        Assert.DoesNotContain("Saldo", email.TextBody);
        Assert.DoesNotContain("136", email.TextBody);   // neither balance (136'110 / 136'970) appears
        Assert.DoesNotContain("Saldo", email.HtmlBody);
    }
}
