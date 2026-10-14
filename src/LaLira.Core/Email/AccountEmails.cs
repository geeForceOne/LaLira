using LaLira.Core.Localization;
using static LaLira.Core.Email.EmailLayout;

namespace LaLira.Core.Email;

// The account emails, as plain text plus a simple HTML version, in the language of t.
public static class AccountEmails
{
    // login: what they sign in with, normally their email address.
    public static EmailMessage Invite(Texts t, string shownName, string login, string link, TimeSpan validFor) => Build(t,
        t["Your {0} account", AppInfo.Name],
        t["Hello {0}, an account has been created for you on {1}, a simple bookkeeping app for your accounts.", shownName, AppInfo.Name],
        t["You sign in with your email address, {0}. Open the link below to choose your password and sign in.", login],
        t["Choose your password"],
        link,
        t["The link works once and expires in {0}. If it has expired, ask your administrator for a new one.", Describe(t, validFor)]);

    public static EmailMessage PasswordReset(Texts t, string login, string link, TimeSpan validFor) => Build(t,
        t["Reset your {0} password", AppInfo.Name],
        t["Someone asked to reset the password for the {0} account {1}.", AppInfo.Name, login],
        t["Open the link below to choose a new password."],
        t["Reset password"],
        link,
        t["The link works once and expires in {0}. If you didn't ask for this, ignore this email; your password stays the same.", Describe(t, validFor)]);

    // To an admin, after someone they invited has set their password.
    public static EmailMessage InviteAccepted(Texts t, string newUserName, string newUserLogin, string usersLink) => Build(t,
        t["{0} joined {1}", newUserName, AppInfo.Name],
        t["{0} ({1}) accepted the invite and set up their {2} account.", newUserName, newUserLogin, AppInfo.Name],
        t["You can see and manage the account on the Users page."],
        t["Open Users"],
        usersLink,
        t["You get this email because \"Notify me when invited users join\" is on under Admin settings."]);

    // To an admin, when someone asks for an account.
    public static EmailMessage AccountRequested(Texts t, string name, string email, string? message, string usersLink) => Build(t,
        t["{0} would like a {1} account", name, AppInfo.Name],
        t["{0} ({1}) asked for an account on {2}.", name, email, AppInfo.Name] + " " +
            (string.IsNullOrWhiteSpace(message) ? t["They didn't add a message."] : t["Their message: \"{0}\"", message]),
        t["Approve or decline the request on the Users page. Approving sends them the usual invite."],
        t["Open Users"],
        usersLink,
        t["You get this email because \"Notify me about account requests\" is on under Admin settings."]);

    // To someone whose account request was declined. note: an optional line from the admin.
    public static EmailMessage AccountRequestDeclined(Texts t, string name, string? note)
    {
        var intro = t["Hello {0}, thank you for your interest in {1}. Your request for an account wasn't approved.", name, AppInfo.Name];
        var extra = string.IsNullOrWhiteSpace(note) ? "" : t["A note from the administrator: {0}", note];
        var subject = t["Your {0} account request", AppInfo.Name];
        return new(subject,
            $"{intro}\n\n{(extra.Length > 0 ? extra + "\n" : "")}",
            Html(subject, Paragraph(intro) + (extra.Length > 0 ? Paragraph(extra, marginBottom: 0) : ""), t.Language));
    }

    public static EmailMessage Test(Texts t, string shownName)
    {
        var subject = t["{0} test email", AppInfo.Name];
        var line = t["Email from {0} works. Invites, password reset links and reports will arrive like this one.", AppInfo.Name];
        return new(subject,
            $"{t["Hello {0},", shownName]}\n\n{line}\n",
            Html(subject, Paragraph(t["Hello {0},", shownName]) + Paragraph(line, marginBottom: 0), t.Language));
    }

    private static EmailMessage Build(Texts t, string subject, string intro, string action, string button, string link, string footer)
    {
        var text = $"{intro}\n\n{action}\n\n{link}\n\n{footer}\n";
        var body =
            Paragraph(intro) +
            Paragraph(action, marginBottom: 24) +
            Button(button, link) +
            $"<p style=\"margin:0 0 16px;font-size:13px;color:{Pencil}\">{Enc(t["If the button doesn't work, copy this address into your browser:"])}<br>" +
            $"<span style=\"word-break:break-all\">{Enc(link)}</span></p>" +
            Paragraph(footer, muted: true, marginBottom: 0);
        return new EmailMessage(subject, text, Html(subject, body, t.Language));
    }

    public static string Describe(Texts t, TimeSpan span) =>
        span.TotalDays >= 1 ? t.Plural((int)Math.Round(span.TotalDays), "{0} day", "{0} days")
        : span.TotalHours >= 1 ? t.Plural((int)Math.Round(span.TotalHours), "{0} hour", "{0} hours")
        : t.Plural((int)Math.Round(span.TotalMinutes), "{0} minute", "{0} minutes");
}
