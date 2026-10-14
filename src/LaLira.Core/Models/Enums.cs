namespace LaLira.Core.Models;

public enum Theme
{
    Dark,
    Light,
    System,
}

public enum EmailSecurity
{
    Auto,
    SslOnConnect,
    StartTls,
    None,
}

// How often a recurring position is booked. Each is multiplied by the rule's interval
// ("every 2 months").
public enum Frequency
{
    Weekly,
    Monthly,
    Quarterly,
    Yearly,
}

// The period an email report covers.
public enum ReportPeriod
{
    Monthly,
    Quarterly,
    Yearly,
}

public enum NoticeKind
{
    // Recurring bookings made late because the app wasn't running on their date.
    CaughtUp,
}
