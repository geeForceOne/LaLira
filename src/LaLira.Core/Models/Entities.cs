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

    // The person's own EODHD key for portfolio prices, encrypted with Data Protection.
    public string? PriceApiKeyProtected { get; set; }

    // The last daily price refresh (UTC), and what went wrong in it, if anything.
    public DateTime? PricesCheckedAt { get; set; }
    public string? PriceError { get; set; }

    // The prices of that refresh were checked against the price alerts.
    public DateTime? AlertsCheckedAt { get; set; }

    // ISO 4217 code everything is also shown in, converted with exchange rates; null: off.
    public string? MainCurrency { get; set; }

    // The overview charts' period in months (0: all), the same for every chart there; null: 5 years.
    public int? OverviewMonths { get; set; }

    // The "Others" band is switched off in the overview charts.
    public bool OthersHidden { get; set; }
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

    // A key from AccountColors; accounts from before colors get one on first use.
    public string? Color { get; set; }

    // Optional, compact upper case ("CH9300762011623852957"); see Ledger.Iban.
    public const int MaxIbanLength = 34;
    public string? Iban { get; set; }

    // Display only: amounts are always stored exactly.
    public bool ShowDecimals { get; set; } = true;
    public bool IncludeInReports { get; set; } = true;

    // The account page lists only this year (and upcoming ones); search still covers every year.
    public bool OnlyCurrentYear { get; set; }

    // In the overview charts: switched off from the legend, or added up with others into one band.
    public bool HiddenInTotals { get; set; }
    public bool InOthers { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Position> Positions { get; set; } = [];
    public List<RecurringRule> RecurringRules { get; set; } = [];
}

// Money in (positive) or out (negative) on a date.
public sealed class Position
{
    public const int MaxDescriptionLength = 200;
    public const int MaxNoteLength = 2000;

    public int Id { get; set; }
    public int AccountId { get; set; }
    public Account? Account { get; set; }
    public DateOnly Date { get; set; }
    public long Amount { get; set; }
    public string Description { get; set; } = "";

    // Optional longer text; shown with a note icon in the list, never in email reports.
    public string? Note { get; set; }

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

// A group of securities, e.g. "ETFs and shares". Not linked to the cash accounts.
public sealed class Portfolio
{
    public const int MaxNameLength = 80;

    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Name { get; set; } = "";
    public int SortOrder { get; set; }

    // In the overview charts, like an account's.
    public bool HiddenInTotals { get; set; }
    public bool InOthers { get; set; }

    // With securities in several currencies: totals shown once, in the main currency.
    public bool ShowInMainCurrency { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<Holding> Holdings { get; set; } = [];
}

// One purchase of a security. Quantity and prices are decimals (fractional shares, prices with
// more than two decimals); totals are rounded to cents when shown. Selling part of it splits it
// into a sold row and a held row.
public sealed class Holding
{
    public const int MaxNameLength = 120;
    public const int MaxValorLength = 12;
    public const int MaxTickerLength = 32;

    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public Portfolio? Portfolio { get; set; }
    public string Name { get; set; } = "";

    // Compact upper case, check digit verified; prices are looked up by it.
    public string Isin { get; set; } = "";

    // The Swiss Valor number; shown only.
    public string? Valor { get; set; }

    // A short name shown next to the name ("UBSG"), from a search or typed. Without one, the
    // ticker of the price's listing shows.
    public string? Ticker { get; set; }

    // ISO 4217 code of the prices.
    public string Currency { get; set; } = "CHF";
    public DateOnly BuyDate { get; set; }
    public decimal Quantity { get; set; }
    public decimal BuyPrice { get; set; }

    // Both set once it's sold.
    public DateOnly? SellDate { get; set; }
    public decimal? SellPrice { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// The last close of a security in one currency, fetched with the person's own key.
public sealed class SecurityPrice
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Isin { get; set; } = "";
    public string Currency { get; set; } = "";
    public decimal Price { get; set; }
    public DateOnly PriceDate { get; set; }

    // Where the price is from, e.g. "SW" (SIX) and the ticker there.
    public string? Exchange { get; set; }
    public string? Ticker { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;

    // The past month ends were fetched once (null: not yet).
    public DateTime? HistoryFetchedAt { get; set; }
}

// A security's last close in a month (the current month: the latest), from the price history
// and every price update since. Portfolio values over time are worked out from these.
public sealed class SecurityMonthPrice
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string Isin { get; set; } = "";
    public string Currency { get; set; } = "";

    // The first of the month; PriceDate is the day the close is from.
    public DateOnly Month { get; set; }
    public DateOnly PriceDate { get; set; }
    public decimal Price { get; set; }
}

// A limit on a security in a portfolio (all its held purchases together). It fires once when the
// limit is reached and is armed again once it's back on the other side.
public sealed class PriceAlert
{
    public int Id { get; set; }
    public int PortfolioId { get; set; }
    public Portfolio? Portfolio { get; set; }
    public string Isin { get; set; } = "";
    public string Currency { get; set; } = "";
    public AlertKind Kind { get; set; }
    public bool Above { get; set; }

    // A price per share or a value in the currency, or a gain in percent.
    public decimal Limit { get; set; }
    public bool Armed { get; set; } = true;
    public DateTime? LastFiredAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

// The rate of one currency in another at the end of a month (the current month: the latest),
// from Frankfurter. Shared by everyone: rates aren't personal.
public sealed class ExchangeRate
{
    public int Id { get; set; }
    public string From { get; set; } = "";
    public string To { get; set; } = "";

    // The first of the month; RateDate is the day the rate is from.
    public DateOnly Month { get; set; }
    public DateOnly RateDate { get; set; }
    public decimal Rate { get; set; }
    public DateTime FetchedAt { get; set; } = DateTime.UtcNow;
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
