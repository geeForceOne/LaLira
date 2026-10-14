using Microsoft.AspNetCore.Identity;

namespace LaLira.Core.Models;

public sealed class AppUser : IdentityUser
{
    public const int MaxDisplayNameLength = 80;

    // Optional; without one the UI uses the first part of the email address.
    public string? DisplayName { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastActiveAt { get; set; }

    // The last version whose "what's new" banner this person closed (see WhatsNewService).
    public string? SeenVersion { get; set; }

    // Admin notifications; only used while the person is an admin.
    public bool NotifyInviteAccepted { get; set; } = true;
    public bool NotifyAccountRequests { get; set; } = true;

    public string ShownName => ShownNameFor(DisplayName, Email, UserName);

    public static string ShownNameFor(string? displayName, string? email, string? userName) =>
        !string.IsNullOrWhiteSpace(displayName) ? displayName.Trim()
        : !string.IsNullOrWhiteSpace(email) ? email.Split('@')[0]
        : userName ?? "?";
}

// One row per user; created on first use with values from the browser.
public sealed class UserSettings
{
    public string UserId { get; set; } = "";
    public Theme Theme { get; set; } = Theme.Dark;

    // UI and email language: "en" or "de".
    public string Language { get; set; } = "en";

    // Number and date formats, e.g. "de-CH". Independent of the language.
    public string RegionFormat { get; set; } = "de-CH";

    // IANA id. Decides when this user's recurring bookings and reports run.
    public string TimeZone { get; set; } = "Europe/Zurich";

    // SHA-256 (hex) of the user's homepage widget token; the token itself is shown once.
    public string? HomepageTokenHash { get; set; }
    public DateTime? HomepageTokenCreatedAt { get; set; }
}

// A ledger account (bank account, savings, cash...). Amounts are in hundredths of the currency
// unit ("cents"), so sums are exact.
public sealed class Account
{
    public const int MaxNameLength = 80;

    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";

    // ISO 4217 code, e.g. "CHF".
    public string Currency { get; set; } = "CHF";
    public long OpeningBalance { get; set; }
    public DateOnly OpeningDate { get; set; }


    // A key from AccountIcons.
    public string Icon { get; set; } = AccountIcons.Default;

    // Display only: amounts are always stored exactly.
    public bool ShowDecimals { get; set; } = true;
    public bool IncludeInReports { get; set; } = true;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Position> Positions { get; set; } = [];
    public List<RecurringRule> RecurringRules { get; set; } = [];
}

// Money in (positive) or out (negative) on a date.
public sealed class Position
{
    public const int MaxDescriptionLength = 200;

    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public DateOnly Date { get; set; }
    public long Amount { get; set; }
    public string Description { get; set; } = "";

    // Set when a recurring rule booked it; with OccurrenceDate it makes each occurrence unique,
    // so a rule never books the same date twice.
    public int? RecurringRuleId { get; set; }
    public DateOnly? OccurrenceDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class RecurringRule
{
    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public long Amount { get; set; }

    // With placeholders such as "Deposit {month}" (see DescriptionTemplate).
    public string DescriptionTemplate { get; set; } = "";
    public Frequency Frequency { get; set; } = Frequency.Monthly;

    // Every N weeks/months/quarters/years.
    public int Interval { get; set; } = 1;

    // For monthly, quarterly and yearly rules; 31 means the last day of each month.
    public int DayOfMonth { get; set; } = 1;
    public DateOnly StartDate { get; set; }
    public DateOnly? EndDate { get; set; }

    // Month names in the description come out in this language ("en" or "de").
    public string Language { get; set; } = "en";
    public bool IsActive { get; set; } = true;

    // The next date to book; null once the rule has run past its end date.
    public DateOnly? NextDue { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class EmailReport
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ReportPeriod Period { get; set; } = ReportPeriod.Monthly;
    public bool IsActive { get; set; } = true;

    // The last day of the last period that was reported (or skipped on creation).
    public DateOnly LastPeriodEnd { get; set; }
    public DateTime? LastSentAt { get; set; }
    public string? LastError { get; set; }

    // After a failed send, the next try waits until then.
    public DateTime? RetryAfter { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// A message for the overview, e.g. "3 bookings caught up". Closed by the user.
public sealed class Notice
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public NoticeKind Kind { get; set; }
    public int Count { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public sealed class EmailSettings
{
    public int Id { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public EmailSecurity Security { get; set; } = EmailSecurity.StartTls;
    public string? UserName { get; set; }
    public string? PasswordProtected { get; set; }
    public string? From { get; set; }
    public string? FromName { get; set; }

    // The app's public address for links in emails, e.g. https://lalira.example.com.
    public string? PublicUrl { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

// Instance-wide settings; a single row with Id 1.
public sealed class SiteSettings
{
    public int Id { get; set; } = 1;
    public bool AllowAccountRequests { get; set; }

    public bool AutoBackups { get; set; } = true;
    public TimeOnly BackupTime { get; set; } = new(3, 0);
    public int BackupKeep { get; set; } = 14;

    // The time zone BackupTime is in: the one of the admin who last saved the backup settings.
    public string BackupTimeZone { get; set; } = "Europe/Zurich";
    public DateTime? LastAutoBackupAt { get; set; }
    public string? LastAutoBackupError { get; set; }
}

public sealed class AccountRequest
{
    public const int MaxEmailLength = 256;
    public const int MaxMessageLength = 1000;

    public int Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Message { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
