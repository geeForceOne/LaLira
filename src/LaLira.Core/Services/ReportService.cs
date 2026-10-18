using LaLira.Core.Data;
using LaLira.Core.Email;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LaLira.Core.Services;

public sealed record ReportView(int Id, ReportPeriod Period, bool IsActive, DateRange NextPeriod, DateTime NextSendLocal, DateTime? LastSentAt, string? LastError);

// Email reports: each person's definitions, and sending them when a period is over. A report
// that couldn't go out (app down, mail server unreachable) is sent later; after a long gap only
// the last few periods are sent, so nobody gets a flood.
public sealed class ReportService(
    IDbContextFactory<AppDbContext> dbFactory, SettingsService settings, UserClock clock, EmailSender sender,
    EmailSettingsService emailSettings, ExchangeRateService rates, ILogger<ReportService> logger)
{
    public const int MaxCatchUp = 3;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromHours(1);

    public async Task<IReadOnlyList<ReportView>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reports = await db.EmailReports.AsNoTracking().Where(r => r.UserId == userId).OrderBy(r => r.Period).ThenBy(r => r.Id).ToListAsync(ct);
        return reports.Select(View).ToList();
    }

    // A new report starts with the current period: the first one goes out when it's over.
    public async Task<ReportView> CreateAsync(string userId, ReportPeriod period, CancellationToken ct = default)
    {
        var today = await clock.TodayAsync(userId, ct);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var report = new EmailReport { UserId = userId, Period = period, LastPeriodEnd = ReportPeriods.Before(period, today).End };
        db.EmailReports.Add(report);
        await db.SaveChangesAsync(ct);
        return View(report);
    }

    public async Task SetActiveAsync(string userId, int reportId, bool active, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var report = await db.EmailReports.FirstOrDefaultAsync(r => r.Id == reportId && r.UserId == userId, ct)
            ?? throw new NotFoundException("That report no longer exists.");
        if (active && !report.IsActive)
        {
            // Switched back on: carry on with the current period, not the ones it was off for.
            var today = await clock.TodayAsync(userId, ct);
            report.LastPeriodEnd = ReportPeriods.Before(report.Period, today).End;
            report.LastError = null;
            report.RetryAfter = null;
        }
        report.IsActive = active;
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int reportId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.EmailReports.Where(r => r.Id == reportId && r.UserId == userId).ExecuteDeleteAsync(ct) == 0)
            throw new NotFoundException("That report no longer exists.");
    }

    // Sends the report for the last complete period now, as a preview. Doesn't change the schedule.
    public async Task<string> SendNowAsync(string userId, int reportId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var report = await db.EmailReports.AsNoTracking().FirstOrDefaultAsync(r => r.Id == reportId && r.UserId == userId, ct)
            ?? throw new NotFoundException("That report no longer exists.");
        var today = await clock.TodayAsync(userId, ct);
        return await SendAsync(report.UserId, report.Period, ReportPeriods.Before(report.Period, today), ct);
    }

    // The scheduler's job. Returns how many reports went out.
    public async Task<int> SendDueAsync(CancellationToken ct = default)
    {
        var utcNow = clock.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var reports = await db.EmailReports.Where(r => r.IsActive && (r.RetryAfter == null || r.RetryAfter <= utcNow)).ToListAsync(ct);
        var sent = 0;
        foreach (var report in reports)
        {
            var localNow = Regions.LocalNow(utcNow, (await settings.GetAsync(report.UserId, ct)).TimeZone);
            var pending = Pending(report, localNow);
            if (pending.Count == 0)
                continue;
            if (!await emailSettings.IsConfiguredAsync(ct))
            {
                report.LastError = "Email isn't set up on this server.";
                report.RetryAfter = utcNow + RetryDelay;
                continue;
            }
            // After a long gap, skip all but the last few.
            if (pending.Count > MaxCatchUp)
            {
                report.LastPeriodEnd = pending[^(MaxCatchUp + 1)].End;
                pending = pending.TakeLast(MaxCatchUp).ToList();
            }
            foreach (var range in pending)
            {
                try
                {
                    await SendAsync(report.UserId, report.Period, range, ct);
                    report.LastPeriodEnd = range.End;
                    report.LastSentAt = utcNow;
                    report.LastError = null;
                    report.RetryAfter = null;
                    sent++;
                }
                catch (Exception ex) when (ex is EmailException or InvalidOperationException)
                {
                    logger.LogWarning(ex, "Sending report {ReportId} failed", report.Id);
                    report.LastError = ex.Message;
                    report.RetryAfter = utcNow + RetryDelay;
                    break;
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return sent;
    }

    // Periods that are over and due (their send time has passed), oldest first.
    internal static List<DateRange> Pending(EmailReport report, DateTime localNow)
    {
        var list = new List<DateRange>();
        var next = ReportPeriods.After(report.Period, report.LastPeriodEnd);
        while (ReportPeriods.SendAt(next) <= localNow && list.Count < 1000)
        {
            list.Add(next);
            next = ReportPeriods.After(report.Period, next.End);
        }
        return list;
    }

    // Builds and sends one report; returns the address it went to.
    private async Task<string> SendAsync(string userId, ReportPeriod period, DateRange range, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new InvalidOperationException("That account no longer exists.");
        if (string.IsNullOrEmpty(user.Email))
            throw new InvalidOperationException("Your account has no email address. Add one first.");

        var s = await settings.GetAsync(userId, ct);
        var t = new Texts(s.Language, Regions.Culture(s.RegionFormat));

        var accounts = await db.Accounts.AsNoTracking().Where(a => a.UserId == userId && a.IncludeInReports && a.OpeningDate <= range.End)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name).ToListAsync(ct);
        var ids = accounts.Select(a => a.Id).ToList();
        var positions = await db.Positions.AsNoTracking().Where(p => ids.Contains(p.AccountId) && p.Date <= range.End)
            .OrderBy(p => p.Date).ThenBy(p => p.Id)
            .Select(p => new { p.AccountId, p.Date, p.Amount, p.Description }).ToListAsync(ct);

        var report = accounts.Select(a =>
        {
            var mine = positions.Where(p => p.AccountId == a.Id).ToList();
            var openedInPeriod = a.OpeningDate >= range.Start;
            var balance = openedInPeriod ? 0 : a.OpeningBalance + mine.Where(p => p.Date < range.Start).Sum(p => p.Amount);
            var start = balance;
            var lines = new List<ReportLine>();
            if (openedInPeriod)
            {
                balance = a.OpeningBalance;
                lines.Add(new ReportLine(a.OpeningDate, t["Opening balance"], a.OpeningBalance, balance, IsOpening: true));
            }
            foreach (var p in mine.Where(p => p.Date >= range.Start))
            {
                balance += p.Amount;
                lines.Add(new ReportLine(p.Date, p.Description, p.Amount, balance));
            }
            return new ReportAccount(a.Name, a.Currency, a.ShowDecimals, start, balance, openedInPeriod, lines);
        }).ToList();

        // With a main currency, the period's money in and out at the rate of its last month.
        ReportTotal? total = null;
        if (report.Count > 0 && await rates.TableAsync(userId, ct) is { } table)
        {
            var month = new DateOnly(range.End.Year, range.End.Month, 1);
            var today = await clock.TodayAsync(userId, ct);
            long? Convert(long cents, string currency) =>
                month >= new DateOnly(today.Year, today.Month, 1) ? table.Convert(cents, currency) : table.Convert(cents, currency, month);
            var converted = report.Where(a => table.Has(a.Currency)).ToList();
            total = new ReportTotal(table.MainCurrency, converted.Sum(a => Convert(a.In, a.Currency) ?? 0), converted.Sum(a => Convert(a.Out, a.Currency) ?? 0),
                report.Select(a => a.Currency).Distinct().Where(c => !table.Has(c)).Order().ToList());
        }

        var publicUrl = (await emailSettings.GetAsync(ct)).PublicUrl;
        var message = ReportEmail.Build(t, user.ShownName, period, range, report, publicUrl, total);
        await sender.SendAsync(user.Email, user.ShownName, message, null, ct);
        logger.LogInformation("Sent {Period} report for {Start}..{End} to user {UserId}", period, range.Start, range.End, userId);
        return user.Email;
    }

    private static ReportView View(EmailReport r)
    {
        var next = ReportPeriods.After(r.Period, r.LastPeriodEnd);
        return new ReportView(r.Id, r.Period, r.IsActive, next, ReportPeriods.SendAt(next), r.LastSentAt, r.LastError);
    }
}
