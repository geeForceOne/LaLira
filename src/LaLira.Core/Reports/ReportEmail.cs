using System.Text;
using LaLira.Core.Email;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using static LaLira.Core.Email.EmailLayout;

namespace LaLira.Core.Reports;

public sealed record ReportLine(DateOnly Date, string Description, long Amount, long Balance, bool IsOpening = false);

public sealed record ReportAccount(string Name, string Currency, bool ShowDecimals, long StartBalance, long EndBalance, bool OpenedInPeriod, IReadOnlyList<ReportLine> Lines)
{
    public long In => Lines.Where(l => !l.IsOpening && l.Amount > 0).Sum(l => l.Amount);
    public long Out => Lines.Where(l => !l.IsOpening && l.Amount < 0).Sum(l => l.Amount);
}

// Everything in the main currency; Missing: currencies without an exchange rate, left out.
public sealed record ReportTotal(string Currency, long In, long Out, IReadOnlyList<string> Missing);

// The report email: money in and out per account, and with a main currency all of it converted.
// No balances and no single bookings (the user's choice).
public static class ReportEmail
{
    public static string PeriodName(Texts t, ReportPeriod period, DateRange range) => period switch
    {
        ReportPeriod.Quarterly => t["Q{0} {1}", (range.Start.Month - 1) / 3 + 1, range.Start.Year],
        ReportPeriod.Yearly => range.Start.Year.ToString(t.Format),
        _ => $"{L.MonthName(t.Language, range.Start.Month)} {range.Start.Year}",
    };

    public static EmailMessage Build(Texts t, string shownName, ReportPeriod period, DateRange range, IReadOnlyList<ReportAccount> accounts, string? appLink,
        ReportTotal? total = null)
    {
        var name = PeriodName(t, period, range);
        var subject = t["{0} report: {1}", AppInfo.Name, name];
        var from = range.Start.ToString("d", t.Format);
        var to = range.End.ToString("d", t.Format);
        string M(long cents, ReportAccount a, bool sign = false) => Money.Format(cents, t.Format, a.ShowDecimals, sign);

        var text = new StringBuilder();
        var html = new StringBuilder();
        var intro = t["Hello {0}, here is your money in and out from {1} to {2}.", shownName, from, to];
        text.AppendLine(intro).AppendLine();
        html.Append(Paragraph(intro, marginBottom: 20));

        if (accounts.Count == 0)
        {
            var none = t["No account is included in email reports. Switch \"Include in email reports\" on in an account's settings."];
            text.AppendLine(none);
            html.Append(Paragraph(none));
        }
        else
        {
            // Money in and out per account. Reports never show balances (the user's choice).
            html.Append($"<table role=\"presentation\" cellpadding=\"0\" cellspacing=\"0\" style=\"width:100%;border-collapse:collapse;font-size:14px;margin:0 0 28px\">");
            html.Append($"<tr style=\"color:{Pencil};font-size:12px;text-transform:uppercase;letter-spacing:.04em\">" +
                        Th(t["Account"]) + Th(t["In"], true) + Th(t["Out"], true) + "</tr>");
            foreach (var a in accounts)
            {
                html.Append($"<tr>{Td($"{Enc(a.Name)} <span style=\"color:{Pencil}\">{a.Currency}</span>")}" +
                            Td(M(a.In, a), true) + Td(M(a.Out, a), true) + "</tr>");
                text.AppendLine($"{a.Name} ({a.Currency}): {t["In"]} {M(a.In, a)}, {t["Out"]} {M(a.Out, a)}");
            }
            if (total is not null)
            {
                var label = t["Total in {0}", total.Currency];
                html.Append($"<tr>{Td($"<strong>{Enc(label)}</strong>", top: true)}" +
                            Td($"<strong>{Money.Format(total.In, t.Format)}</strong>", true, true) +
                            Td($"<strong>{Money.Format(total.Out, t.Format)}</strong>", true, true) + "</tr>");
                text.AppendLine($"{label}: {t["In"]} {Money.Format(total.In, t.Format)}, {t["Out"]} {Money.Format(total.Out, t.Format)}");
            }
            html.Append("</table>");
            if (total is { Missing.Count: > 0 })
            {
                var note = t["Without {0}: no exchange rate.", string.Join(", ", total.Missing)];
                html.Append(Paragraph(note, muted: true));
                text.AppendLine(note);
            }
            text.AppendLine();
        }

        var footer = t["You get this email because of your {0} report in {1}. You can change or stop it there under Reports.", t[period switch
        {
            ReportPeriod.Quarterly => "quarterly",
            ReportPeriod.Yearly => "yearly",
            _ => "monthly",
        }], AppInfo.Name];
        if (appLink is not null)
            html.Append(Button(t["Open {0}", AppInfo.Name], appLink));
        html.Append(Paragraph(footer, muted: true, marginBottom: 0));
        text.AppendLine(footer);
        if (appLink is not null)
            text.AppendLine(appLink);

        return new EmailMessage(subject, text.ToString(), Html(subject, html.ToString(), t.Language, maxWidth: 640));
    }

    private static string Th(string s, bool right = false) =>
        $"<th style=\"text-align:{(right ? "right" : "left")};font-weight:500;padding:6px 8px;border-bottom:1px solid {Rule}\">{Enc(s)}</th>";

    private static string Td(string html, bool right = false, bool top = false) =>
        $"<td style=\"text-align:{(right ? "right" : "left")};padding:7px 8px;border-{(top ? "top:2px solid " + Ink : "bottom:1px solid " + Rule)};" +
        $"vertical-align:top;{(right ? "white-space:nowrap;font-variant-numeric:tabular-nums;" : "")}\">{html}</td>";
}
