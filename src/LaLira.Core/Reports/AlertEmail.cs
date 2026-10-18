using System.Text;
using LaLira.Core.Email;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Services;
using static LaLira.Core.Email.EmailLayout;

namespace LaLira.Core.Reports;

// The email for price alerts reached at a price update: per alert the security, its limit, the
// price and what all its shares are worth now.
public static class AlertEmail
{
    // "Price above 35.00", "Gain below −10.00%", "Value above 10'000.00 CHF"
    public static string Describe(Texts t, AlertKind kind, bool above, decimal limit, string currency) => (kind, above) switch
    {
        (AlertKind.Price, true) => t["Price above {0}", Decimals.FormatPrice(limit, t.Format)],
        (AlertKind.Price, false) => t["Price below {0}", Decimals.FormatPrice(limit, t.Format)],
        (AlertKind.GainPercent, true) => t["Gain above {0}", Decimals.FormatPercent(limit, t.Format)],
        (AlertKind.GainPercent, false) => t["Gain below {0}", Decimals.FormatPercent(limit, t.Format)],
        (_, true) => t["Value above {0}", $"{Money.Format(Money.FromDecimal(limit), t.Format)} {currency}"],
        _ => t["Value below {0}", $"{Money.Format(Money.FromDecimal(limit), t.Format)} {currency}"],
    };

    public static EmailMessage Build(Texts t, string shownName, IReadOnlyList<FiredAlert> reached, string? appLink)
    {
        var first = reached[0];
        var subject = reached.Count == 1
            ? t["{0} alert: {1}", AppInfo.Name, $"{first.Security.Name} · {Describe(t, first.Kind, first.Above, first.Limit, first.Security.Currency)}"]
            : t["{0} alert: {1} limits reached", AppInfo.Name, reached.Count];
        var text = new StringBuilder();
        var html = new StringBuilder();
        var intro = reached.Count == 1
            ? t["Hello {0}, a price alert was reached at the last price update.", shownName]
            : t["Hello {0}, {1} price alerts were reached at the last price update.", shownName, reached.Count];
        text.AppendLine(intro).AppendLine();
        html.Append(Paragraph(intro, marginBottom: 20));

        foreach (var a in reached)
        {
            var s = a.Security;
            var name = s.Ticker is null ? s.Name : $"{s.Name} ({s.Ticker})";
            var limit = Describe(t, a.Kind, a.Above, a.Limit, s.Currency);
            var price = s.Price is { } p ? $"{Decimals.FormatPrice(p, t.Format)} {s.Currency}" : "–";
            var date = s.PriceDate?.ToString("d", t.Format) ?? "";
            var shares = s.Value is { } v
                ? t["Your {0} shares: {1}", Decimals.Format(s.Quantity, t.Format), $"{Money.Format(v, t.Format)} {s.Currency}"]
                  + (s.Gain is { } g ? $", {Money.Format(g, t.Format, sign: true)}" + (s.GainPercent is { } pct ? $" ({Decimals.FormatPercent(pct, t.Format)})" : "") : "")
                : "";

            html.Append($"<h2 style=\"font-size:16px;margin:0 0 4px\">{Enc(name)}</h2>");
            html.Append($"<p style=\"margin:0 0 4px;color:{Pencil};font-size:13px\">{Enc(a.PortfolioName)} · {Enc(s.Isin)}</p>");
            html.Append($"<p style=\"margin:0 0 4px\"><strong>{Enc(limit)}</strong></p>");
            html.Append($"<p style=\"margin:0 0 4px\">{Enc(t["Price {0} (close of {1})", price, date])}</p>");
            if (shares.Length > 0)
                html.Append($"<p style=\"margin:0 0 24px\">{Enc(shares)}</p>");
            else
                html.Append("<div style=\"height:20px\"></div>");

            text.AppendLine($"== {name} ==");
            text.AppendLine($"{a.PortfolioName} · {s.Isin}");
            text.AppendLine(limit);
            text.AppendLine(t["Price {0} (close of {1})", price, date]);
            if (shares.Length > 0)
                text.AppendLine(shares);
            text.AppendLine();
        }

        var footer = t["You get this email because of your price alerts in {0}. You can change or remove them on the portfolio page with the bell button.", AppInfo.Name];
        if (appLink is not null)
        {
            var link = reached.Select(r => r.PortfolioId).Distinct().Count() == 1 ? $"{appLink.TrimEnd('/')}/portfolios/{first.PortfolioId}" : appLink;
            html.Append(Button(t["Open {0}", AppInfo.Name], link));
            text.AppendLine(link);
        }
        html.Append(Paragraph(footer, muted: true, marginBottom: 0));
        text.AppendLine(footer);
        return new EmailMessage(subject, text.ToString(), Html(subject, html.ToString(), t.Language, maxWidth: 640));
    }
}
