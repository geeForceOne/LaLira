using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

// One account on the overview: balance today, this month's money in and out, and its balance at
// the end of each of the last months (oldest first) for the small chart.
public sealed record AccountCard(
    AccountInfo Account, long BalanceToday, long MonthIn, long MonthOut, int UpcomingCount, IReadOnlyList<long> MonthEndBalances);

// MonthEnds: the sum of the currency's accounts at the end of every month from the earliest
// opening to today, oldest first (an account counts from its opening month).
public sealed record CurrencyTotal(string Currency, long Balance, long MonthIn, long MonthOut,
    IReadOnlyList<(DateOnly Month, long Balance)> MonthEnds);

public sealed record OverviewView(DateOnly Today, IReadOnlyList<AccountCard> Accounts, IReadOnlyList<CurrencyTotal> Totals, int CaughtUp);

public sealed record MonthFlow(string Currency, long In, long Out);

// The start page's numbers, and the homepage widget's month figures.
public sealed class OverviewService(IDbContextFactory<AppDbContext> dbFactory, AccountService accounts, UserClock clock)
{
    public const int ChartMonths = 12;

    public async Task<OverviewView> GetAsync(string userId, CancellationToken ct = default)
    {
        var today = await clock.TodayAsync(userId, ct);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var list = await accounts.ListAsync(userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = list.Select(a => a.Id).ToList();
        var positions = await db.Positions.AsNoTracking().Where(p => ids.Contains(p.AccountId))
            .Select(p => new { p.AccountId, p.Date, p.Amount }).ToListAsync(ct);
        var byAccount = positions.ToLookup(p => p.AccountId, p => (p.Date, p.Amount));

        var cards = list.Select(a =>
        {
            var mine = byAccount[a.Id].ToList();
            var monthMoves = mine.Where(p => p.Date >= monthStart && p.Date <= today).ToList();
            var chart = Enumerable.Range(0, ChartMonths)
                .Select(i => monthStart.AddMonths(i - ChartMonths + 1))
                .Select(m => m.AddMonths(1).AddDays(-1))
                .Select(end => end > today ? today : end)
                .Where(end => end >= a.OpeningDate || end.Year == a.OpeningDate.Year && end.Month == a.OpeningDate.Month)
                .Select(end => LedgerBuilder.BalanceAt(a.OpeningBalance, a.OpeningDate, mine, end))
                .ToList();
            return new AccountCard(a,
                LedgerBuilder.BalanceAt(a.OpeningBalance, a.OpeningDate, mine, today),
                monthMoves.Where(p => p.Amount > 0).Sum(p => p.Amount),
                monthMoves.Where(p => p.Amount < 0).Sum(p => p.Amount),
                mine.Count(p => p.Date > today),
                chart);
        }).ToList();

        var totals = cards.GroupBy(c => c.Account.Currency)
            .Select(g => new CurrencyTotal(g.Key, g.Sum(c => c.BalanceToday), g.Sum(c => c.MonthIn), g.Sum(c => c.MonthOut),
                SumMonthEnds(g.Select(c => LedgerBuilder.MonthEnds(c.Account.OpeningBalance, c.Account.OpeningDate,
                    byAccount[c.Account.Id].Select(p => new LedgerPosition(0, p.Date, p.Amount, "", null)), today)))))
            .OrderByDescending(t => cards.Count(c => c.Account.Currency == t.Currency)).ThenBy(t => t.Currency)
            .ToList();

        var caughtUp = await db.Notices.AsNoTracking().Where(n => n.UserId == userId && n.Kind == NoticeKind.CaughtUp)
            .SumAsync(n => n.Count, ct);
        return new OverviewView(today, cards, totals, caughtUp);
    }

    // Adds month-end series that start in different months; before its first month a series counts 0.
    public static IReadOnlyList<(DateOnly Month, long Balance)> SumMonthEnds(IEnumerable<IReadOnlyList<(DateOnly Month, long Balance)>> series)
    {
        var sums = new SortedDictionary<DateOnly, long>();
        foreach (var s in series)
            foreach (var (month, balance) in s)
                sums[month] = sums.GetValueOrDefault(month) + balance;
        return sums.Select(kv => (kv.Key, kv.Value)).ToList();
    }

    public async Task DismissCaughtUpAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Notices.Where(n => n.UserId == userId && n.Kind == NoticeKind.CaughtUp).ExecuteDeleteAsync(ct);
    }

    // This month's money in and out up to today, per currency (the homepage widget).
    public async Task<(DateOnly Today, IReadOnlyList<MonthFlow> Flows)> MonthFlowAsync(string userId, CancellationToken ct = default)
    {
        var today = await clock.TodayAsync(userId, ct);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moves = await db.Positions.AsNoTracking()
            .Where(p => p.Account!.UserId == userId && p.Date >= monthStart && p.Date <= today)
            .Select(p => new { p.Account!.Currency, p.Amount }).ToListAsync(ct);
        var currencies = await db.Accounts.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.Currency).Distinct().ToListAsync(ct);
        return (today, currencies.Order().Select(c => new MonthFlow(c,
            moves.Where(m => m.Currency == c && m.Amount > 0).Sum(m => m.Amount),
            moves.Where(m => m.Currency == c && m.Amount < 0).Sum(m => m.Amount))).ToList());
    }
}
