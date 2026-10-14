using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LaLira.Core.Services;

public sealed record RuleView(
    int Id, int AccountId, string AccountName, string Currency, bool ShowDecimals, long Amount, string DescriptionTemplate,
    Frequency Frequency, int Interval, int DayOfMonth, DateOnly StartDate, DateOnly? EndDate, string Language,
    bool IsActive, DateOnly? NextDue);

public sealed record RuleInput(
    int AccountId, long Amount, string DescriptionTemplate, Frequency Frequency, int Interval, int DayOfMonth,
    DateOnly StartDate, DateOnly? EndDate, string Language, bool IsActive);

// A booking the rules will make soon, for the overview.
public sealed record UpcomingBooking(int RuleId, int AccountId, string AccountName, string Currency, bool ShowDecimals, DateOnly Date, long Amount, string Description);

// Recurring positions: the rules, and booking what's due. Every due date is booked exactly once
// with its own date, also when the app was down on that day (caught up later) and when a new
// rule starts in the past (backfilled right away). One booking pass runs at a time.
public sealed class RecurringService(
    IDbContextFactory<AppDbContext> dbFactory, AccountService accounts, SettingsService settings, UserClock clock,
    ILogger<RecurringService> logger)
{
    public const int MaxInterval = 52;

    private readonly SemaphoreSlim _booking = new(1, 1);

    public async Task<IReadOnlyList<RuleView>> ListAsync(string userId, int? accountId = null, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var query = db.RecurringRules.AsNoTracking().Where(r => r.Account!.UserId == userId);
        if (accountId is { } id)
            query = query.Where(r => r.AccountId == id);
        var rules = await query.Include(r => r.Account).ToListAsync(ct);
        return rules.OrderBy(r => r.Account!.SortOrder).ThenBy(r => r.Account!.Name)
            .ThenBy(r => r.NextDue ?? DateOnly.MaxValue).Select(View).ToList();
    }

    public async Task<RuleView> GetAsync(string userId, int ruleId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.RecurringRules.AsNoTracking().Include(r => r.Account)
            .FirstOrDefaultAsync(r => r.Id == ruleId && r.Account!.UserId == userId, ct)
            ?? throw new NotFoundException("That recurring position no longer exists.");
        return View(rule);
    }

    // Saves a new rule and books its dates up to today right away. Returns how many were booked.
    public async Task<(RuleView Rule, int Booked)> CreateAsync(string userId, RuleInput input, CancellationToken ct = default)
    {
        var account = await accounts.GetAsync(userId, input.AccountId, ct);
        var clean = Validate(input, account);
        int ruleId;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rule = new RecurringRule { AccountId = account.Id };
            Apply(rule, clean);
            rule.NextDue = RuleSchedule.Of(rule).NextOnOrAfter(rule.StartDate);
            db.RecurringRules.Add(rule);
            await db.SaveChangesAsync(ct);
            ruleId = rule.Id;
        }
        var booked = await BookRuleAsync(ruleId, countAsCaughtUp: false, ct);
        return (await GetAsync(userId, ruleId, ct), booked);
    }

    // Changes a rule. A changed schedule continues after the last date already booked; dates up
    // to today that the new schedule adds are booked right away. Returns how many were booked.
    public async Task<(RuleView Rule, int Booked)> UpdateAsync(string userId, int ruleId, RuleInput input, CancellationToken ct = default)
    {
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var rule = await db.RecurringRules.Include(r => r.Account)
                .FirstOrDefaultAsync(r => r.Id == ruleId && r.Account!.UserId == userId, ct)
                ?? throw new NotFoundException("That recurring position no longer exists.");
            var account = await accounts.GetAsync(userId, input.AccountId, ct);
            var clean = Validate(input, account);
            var wasPaused = !rule.IsActive;
            Apply(rule, clean);
            rule.AccountId = account.Id;

            var lastBooked = await db.Positions.Where(p => p.RecurringRuleId == ruleId)
                .MaxAsync(p => (DateOnly?)p.OccurrenceDate, ct);
            var from = lastBooked is { } last && last >= rule.StartDate ? last.AddDays(1) : rule.StartDate;
            // A paused rule that's switched back on carries on from today; the dates it was
            // paused for stay unbooked.
            var today = await clock.TodayAsync(userId, ct);
            if (wasPaused && rule.IsActive && from < today)
                from = today;
            rule.NextDue = RuleSchedule.Of(rule).NextOnOrAfter(from);
            await db.SaveChangesAsync(ct);
        }
        var booked = await BookRuleAsync(ruleId, countAsCaughtUp: false, ct);
        return (await GetAsync(userId, ruleId, ct), booked);
    }

    // Deletes the rule; what it already booked stays.
    public async Task DeleteAsync(string userId, int ruleId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rule = await db.RecurringRules.FirstOrDefaultAsync(r => r.Id == ruleId && r.Account!.UserId == userId, ct)
            ?? throw new NotFoundException("That recurring position no longer exists.");
        db.RecurringRules.Remove(rule);
        await db.SaveChangesAsync(ct);
    }

    // What the description will say on a given date, for the dialog's preview.
    public async Task<string> PreviewAsync(string userId, string template, DateOnly date, string language, CancellationToken ct = default)
    {
        var s = await settings.GetAsync(userId, ct);
        return DescriptionTemplate.Render(template, date, language, Regions.Culture(s.RegionFormat));
    }

    // The next bookings across all of the person's active rules, soonest first.
    public async Task<IReadOnlyList<UpcomingBooking>> UpcomingAsync(string userId, int days, int max, CancellationToken ct = default)
    {
        var today = await clock.TodayAsync(userId, ct);
        var s = await settings.GetAsync(userId, ct);
        var culture = Regions.Culture(s.RegionFormat);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var rules = await db.RecurringRules.AsNoTracking().Include(r => r.Account)
            .Where(r => r.Account!.UserId == userId && r.IsActive && r.NextDue != null).ToListAsync(ct);
        return rules
            .SelectMany(r => RuleSchedule.Of(r).Between(r.NextDue!.Value, today.AddDays(days)).Take(max)
                .Select(d => new UpcomingBooking(r.Id, r.AccountId, r.Account!.Name, r.Account.Currency, r.Account.ShowDecimals, d, r.Amount,
                    DescriptionTemplate.Render(r.DescriptionTemplate, d, r.Language, culture))))
            .OrderBy(u => u.Date).ThenBy(u => u.AccountName)
            .Take(max).ToList();
    }

    // The scheduler's job: books every due date of every active rule, in each owner's time zone.
    // Dates before today were missed while the app was down; they're counted in a notice.
    public async Task<int> BookDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var due = await db.RecurringRules.AsNoTracking()
            .Where(r => r.IsActive && r.NextDue != null)
            .Select(r => new { r.Id, r.NextDue, r.Account!.UserId }).ToListAsync(ct);

        var total = 0;
        foreach (var r in due)
        {
            var today = Regions.LocalToday(clock.UtcNow, (await settings.GetAsync(r.UserId, ct)).TimeZone);
            if (r.NextDue > today)
                continue;
            try
            {
                total += await BookRuleAsync(r.Id, countAsCaughtUp: true, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Booking recurring rule {RuleId} failed", r.Id);
            }
        }
        return total;
    }

    // Books the rule's dates from NextDue up to the owner's today, each once.
    private async Task<int> BookRuleAsync(int ruleId, bool countAsCaughtUp, CancellationToken ct)
    {
        await _booking.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var rule = await db.RecurringRules.Include(r => r.Account).FirstOrDefaultAsync(r => r.Id == ruleId, ct);
            if (rule is null || !rule.IsActive || rule.NextDue is null)
                return 0;

            var userId = rule.Account!.UserId;
            var s = await settings.GetAsync(userId, ct);
            var today = Regions.LocalToday(clock.UtcNow, s.TimeZone);
            var culture = Regions.Culture(s.RegionFormat);
            var schedule = RuleSchedule.Of(rule);

            var already = (await db.Positions.Where(p => p.RecurringRuleId == ruleId && p.OccurrenceDate != null)
                .Select(p => p.OccurrenceDate!.Value).ToListAsync(ct)).ToHashSet();

            var booked = 0;
            var late = 0;
            while (rule.NextDue is { } date && date <= today)
            {
                if (!already.Contains(date))
                {
                    db.Positions.Add(new Position
                    {
                        AccountId = rule.AccountId,
                        Date = date,
                        Amount = rule.Amount,
                        Description = DescriptionTemplate.Render(rule.DescriptionTemplate, date, rule.Language, culture),
                        RecurringRuleId = rule.Id,
                        OccurrenceDate = date,
                    });
                    booked++;
                    if (date < today)
                        late++;
                }
                rule.NextDue = schedule.NextAfter(date);
            }

            if (countAsCaughtUp && late > 0)
            {
                var notice = await db.Notices.FirstOrDefaultAsync(n => n.UserId == userId && n.Kind == NoticeKind.CaughtUp, ct);
                if (notice is null)
                    db.Notices.Add(new Notice { UserId = userId, Kind = NoticeKind.CaughtUp, Count = late });
                else
                {
                    notice.Count += late;
                    notice.CreatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            if (booked > 0)
                logger.LogInformation("Recurring rule {RuleId} booked {Count} position(s), {Late} caught up", rule.Id, booked, late);
            return booked;
        }
        finally
        {
            _booking.Release();
        }
    }

    private static void Apply(RecurringRule rule, RuleInput input)
    {
        rule.Amount = input.Amount;
        rule.DescriptionTemplate = input.DescriptionTemplate;
        rule.Frequency = input.Frequency;
        rule.Interval = input.Interval;
        rule.DayOfMonth = input.DayOfMonth;
        rule.StartDate = input.StartDate;
        rule.EndDate = input.EndDate;
        rule.Language = input.Language;
        rule.IsActive = input.IsActive;
    }

    private static RuleInput Validate(RuleInput input, AccountInfo account)
    {
        var template = input.DescriptionTemplate.Trim();
        if (template.Length == 0)
            throw new InputException("Enter a description.");
        if (template.Length > Position.MaxDescriptionLength)
            throw new InputException("Keep the description under {0} characters.", Position.MaxDescriptionLength);
        if (input.Amount == 0)
            throw new InputException("Enter an amount other than zero.");
        if (Math.Abs(input.Amount) > Money.MaxAbsolute)
            throw new InputException("That amount is too large.");
        if (input.Interval is < 1 or > MaxInterval)
            throw new InputException("Repeat every 1 to {0} periods.", MaxInterval);
        if (input.DayOfMonth is < 1 or > RuleSchedule.LastDay)
            throw new InputException("Pick a day of the month from 1 to 31.");
        if (input.StartDate < account.OpeningDate)
            throw new InputException("The start date is before the account's opening date ({0}).", account.OpeningDate);
        if (input.EndDate is { } end && end < input.StartDate)
            throw new InputException("The end date is before the start date.");
        // Weekly rules take their weekday from the start date; the day of month doesn't apply.
        var day = input.Frequency == Frequency.Weekly ? input.StartDate.Day : input.DayOfMonth;
        return input with { DescriptionTemplate = template, Language = L.Normalize(input.Language), DayOfMonth = day };
    }

    private static RuleView View(RecurringRule r) => new(
        r.Id, r.AccountId, r.Account!.Name, r.Account.Currency, r.Account.ShowDecimals, r.Amount, r.DescriptionTemplate,
        r.Frequency, r.Interval, r.DayOfMonth, r.StartDate, r.EndDate, r.Language, r.IsActive, r.NextDue);
}
