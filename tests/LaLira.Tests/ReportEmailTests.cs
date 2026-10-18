using System.Globalization;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Reports;

namespace LaLira.Tests;

public class ReportEmailTests
{
    [Fact]
    public void ReportShowsInAndOutButNoBalancesOrBookings()
    {
        var t = new Texts("de", CultureInfo.GetCultureInfo("de-CH"));
        var range = new DateRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var account = new ReportAccount("Sparkonto", "CHF", true, 13611000, 13697000, false,
            [new ReportLine(new DateOnly(2026, 9, 1), "September", 86000, 13697000)]);
        var email = ReportEmail.Build(t, "Anna", ReportPeriod.Monthly, range, [account], null);

        Assert.Contains("hier sind Ihre Eingänge und Ausgänge vom 01.09.2026 bis 30.09.2026", email.TextBody);
        Assert.Contains("Eingang 860.00", email.TextBody);
        // Only the summary: no single bookings and no converted total without a main currency.
        Assert.DoesNotContain("01.09.2026  September", email.TextBody);
        Assert.DoesNotContain("Total in", email.TextBody);
        Assert.DoesNotContain("Saldo", email.TextBody);
        Assert.DoesNotContain("136", email.TextBody);   // neither balance (136'110 / 136'970) appears
        Assert.DoesNotContain("Saldo", email.HtmlBody);
    }

    [Fact]
    public void ReportAddsTheTotalInTheMainCurrency()
    {
        var t = new Texts("en", CultureInfo.GetCultureInfo("de-CH"));
        var range = new DateRange(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var account = new ReportAccount("Euro", "EUR", true, 0, 0, false, [new ReportLine(new DateOnly(2026, 9, 1), "Pay", 10000, 0)]);
        var email = ReportEmail.Build(t, "Anna", ReportPeriod.Monthly, range, [account], null, new ReportTotal("CHF", 9300, -500, ["XXX"]));

        Assert.Contains("Total in CHF: In 93.00, Out −5.00", email.TextBody);
        Assert.Contains("Without XXX: no exchange rate.", email.TextBody);
    }
}
