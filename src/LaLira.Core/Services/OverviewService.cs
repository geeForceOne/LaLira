using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

// One account on the overview: balance today, this month's money in and out, and its balance at
// the end of every month since its opening (oldest first) for the small chart, which shows the
// overview's period of it.
public sealed record AccountCard(
    AccountInfo Account, long BalanceToday, long MonthIn, long MonthOut, int UpcomingCount, IReadOnlyList<long> MonthEndBalances);

// One account (or portfolio) in a stacked chart: its value at each of the chart's month ends,
// 0 before it existed. Color is an AccountColors key. Key names it for switching it off from the
// legend ("a12", "p3", or OverviewService.OthersKey for the accounts and portfolios added up as
// "Others"); a Hidden band isn't drawn.
public sealed record ChartBand(string Name, string Color, IReadOnlyList<long> Values, string Key = "", bool Hidden = false);

// MonthEnds: the sum of the currency's accounts at the end of every month from the earliest
// opening to today, oldest first (an account counts from its opening month); Bands: each
// account's part of it, in the accounts' order.
public sealed record CurrencyTotal(string Currency, long Balance, long MonthIn, long MonthOut,
    IReadOnlyList<(DateOnly Month, long Balance)> MonthEnds, IReadOnlyList<ChartBand> Bands);

// Everything converted into the person's main currency: accounts at each month end's rate (this
// month at the newest), portfolios as far back as their values are recorded. Missing: currencies
// without an exchange rate, left out.
public sealed record MainTotal(string Currency, long Balance, long Accounts, long Portfolios, long MonthIn, long MonthOut,
    IReadOnlyList<(DateOnly Month, long Balance)> MonthEnds, IReadOnlyList<ChartBand> Bands, DateOnly? RatesDate,
    IReadOnlyList<string> Missing, bool HasPortfolios);

// Months: the period of every chart on the overview (0: all).
public sealed record OverviewView(DateOnly Today, IReadOnlyList<AccountCard> Accounts, IReadOnlyList<CurrencyTotal> Totals, int CaughtUp,
    MainTotal? Main = null, int AlertsReached = 0, int Months = OverviewService.DefaultMonths);

public sealed record MonthFlow(string Currency, long In, long Out);

// The start page's numbers, and the homepage widget's month figures.
public sealed class OverviewService(
    IDbContextFactory<AppDbContext> dbFactory, AccountService accounts, UserClock clock, PortfolioService portfolios, ExchangeRateService rates)
{
    public const int DefaultMonths = 60;
    public const string OthersKey = "others";

    // The periods the overview charts can show; 0 is everything.
    public static readonly int[] Periods = [12, 60, 0];

    // withMain: also the main-currency total (the sidebar doesn't need it).
    public async Task<OverviewView> GetAsync(string userId, CancellationToken ct = default, bool withMain = true)
    {
        var today = await clock.TodayAsync(userId, ct);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var list = await accounts.ListAsync(userId, ct);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var ids = list.Select(a => a.Id).ToList();
        var positions = await db.Positions.AsNoTracking().Where(p => ids.Contains(p.AccountId))
            .Select(p => new { p.AccountId, p.Date, p.Amount }).ToListAsync(ct);
        var byAccount = positions.ToLookup(p => p.AccountId, p => (p.Date, p.Amount));
        var monthEnds = list.ToDictionary(a => a.Id, a => LedgerBuilder.MonthEnds(a.OpeningBalance, a.OpeningDate,
            byAccount[a.Id].Select(p => new LedgerPosition(0, p.Date, p.Amount, "", null)), today));

        var cards = list.Select(a =>
        {
            var mine = byAccount[a.Id].ToList();
            var monthMoves = mine.Where(p => p.Date >= monthStart && p.Date <= today).ToList();
            var chart = monthEnds[a.Id].Select(m => m.Balance).ToList();
            return new AccountCard(a,
                LedgerBuilder.BalanceAt(a.OpeningBalance, a.OpeningDate, mine, today),
                monthMoves.Where(p => p.Amount > 0).Sum(p => p.Amount),
                monthMoves.Where(p => p.Amount < 0).Sum(p => p.Amount),
                mine.Count(p => p.Date > today),
                chart);
        }).ToList();

        var prefs = await db.UserSettings.AsNoTracking().Where(s => s.UserId == userId)
            .Select(s => new { s.OverviewMonths, s.OthersHidden }).FirstOrDefaultAsync(ct);
        var othersHidden = prefs?.OthersHidden ?? false;

        var totals = cards.GroupBy(c => c.Account.Currency)
            .Select(g =>
            {
                var sum = SumMonthEnds(g.Select(c => monthEnds[c.Account.Id]));
                var months = sum.Select(m => m.Month).ToList();
                var bands = Bands(g.Select(c => Part(c.Account, monthEnds[c.Account.Id].ToDictionary(m => m.Month, m => m.Balance))), months, othersHidden);
                return new CurrencyTotal(g.Key, g.Sum(c => c.BalanceToday), g.Sum(c => c.MonthIn), g.Sum(c => c.MonthOut), sum, bands);
            })
            .OrderByDescending(t => cards.Count(c => c.Account.Currency == t.Currency)).ThenBy(t => t.Currency)
            .ToList();

        var notices = await db.Notices.AsNoTracking().Where(n => n.UserId == userId)
            .GroupBy(n => n.Kind).Select(g => new { Kind = g.Key, Count = g.Sum(n => n.Count) }).ToListAsync(ct);
        var main = withMain ? await MainAsync(userId, today, cards, monthEnds, othersHidden, ct) : null;
        return new OverviewView(today, cards, totals,
            notices.FirstOrDefault(n => n.Kind == NoticeKind.CaughtUp)?.Count ?? 0, main,
            notices.FirstOrDefault(n => n.Kind == NoticeKind.PriceAlert)?.Count ?? 0,
            prefs?.OverviewMonths is { } chosen && Periods.Contains(chosen) ? chosen : DefaultMonths);
    }

    private async Task<MainTotal?> MainAsync(string userId, DateOnly today, IReadOnlyList<AccountCard> cards,
        Dictionary<int, IReadOnlyList<(DateOnly Month, long Balance)>> monthEnds, bool othersHidden, CancellationToken ct)
    {
        if (await rates.TableAsync(userId, ct) is not { } table)
            return null;
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var portfolioList = await portfolios.ListAsync(userId, ct);
        var histories = await portfolios.HistoriesAsync(userId, ct);
        // Portfolios with something held at some point.
        var withValues = portfolioList.Where(p => histories.GetValueOrDefault(p.Id) is { } h && h.Any(x => x.Months.Any(m => m.Value != 0))).ToList();
        // Nothing to convert or add up: the currency's own total says it all.
        if (withValues.Count == 0 && cards.All(c => c.Account.Currency == table.MainCurrency))
            return null;

        long Convert(long cents, string currency, DateOnly month) =>
            (month >= thisMonth ? table.Convert(cents, currency) : table.Convert(cents, currency, month)) ?? 0;

        var missing = cards.Select(c => c.Account.Currency)
            .Concat(withValues.SelectMany(p => histories[p.Id].Select(h => h.Currency)))
            .Distinct().Where(c => !table.Has(c)).Order().ToList();
        var converted = cards.Where(c => table.Has(c.Account.Currency)).ToList();
        var first = converted.Select(c => new DateOnly(c.Account.OpeningDate.Year, c.Account.OpeningDate.Month, 1))
            .Concat(withValues.SelectMany(p => histories[p.Id].Where(h => h.Months.Count > 0).Select(h => h.Months[0].Month)))
            .DefaultIfEmpty(thisMonth).Min();
        var months = new List<DateOnly>();
        for (var m = first; m <= thisMonth; m = m.AddMonths(1))
            months.Add(m);

        var accountParts = converted.Select(c => Part(c.Account,
            monthEnds[c.Account.Id].ToDictionary(m => m.Month, m => Convert(m.Balance, c.Account.Currency, m.Month)))).ToList();
        var portfolioParts = new List<BandPart>();
        foreach (var p in withValues)
        {
            var values = new Dictionary<DateOnly, long>();
            foreach (var h in histories[p.Id].Where(h => table.Has(h.Currency)))
                foreach (var m in h.Months)
                    values[m.Month] = values.GetValueOrDefault(m.Month) + Convert(m.Value, h.Currency, m.Month);
            portfolioParts.Add(new BandPart($"p{p.Id}", p.Name, p.Color, p.InOthers, p.HiddenInTotals, values));
        }

        var bands = Bands(accountParts.Concat(portfolioParts), months, othersHidden);
        var series = months.Select((m, i) => (m, bands.Sum(b => b.Values[i]))).ToList();
        var accountsNow = accountParts.Sum(a => a.Values.GetValueOrDefault(thisMonth));
        var portfoliosNow = portfolioParts.Sum(p => p.Values.GetValueOrDefault(thisMonth));
        return new MainTotal(table.MainCurrency, accountsNow + portfoliosNow, accountsNow, portfoliosNow,
            converted.Sum(c => table.Convert(c.MonthIn, c.Account.Currency) ?? 0),
            converted.Sum(c => table.Convert(c.MonthOut, c.Account.Currency) ?? 0),
            series, bands, table.LatestDate, missing, portfolioParts.Count > 0);
    }

    // An account or portfolio before it becomes a band: its values by month.
    private sealed record BandPart(string Key, string Name, string Color, bool InOthers, bool Hidden, IReadOnlyDictionary<DateOnly, long> Values);

    private static BandPart Part(AccountInfo a, IReadOnlyDictionary<DateOnly, long> values) =>
        new($"a{a.Id}", a.Name, a.Color, a.InOthers, a.HiddenInTotals, values);

    // A band per part at the given months (0 before its first month), those marked "Others" added
    // up into one band at the end.
    private static List<ChartBand> Bands(IEnumerable<BandPart> parts, IReadOnlyList<DateOnly> months, bool othersHidden)
    {
        var list = parts.ToList();
        var bands = list.Where(p => !p.InOthers)
            .Select(p => new ChartBand(p.Name, p.Color, months.Select(m => p.Values.GetValueOrDefault(m)).ToList(), p.Key, p.Hidden)).ToList();
        var others = list.Where(p => p.InOthers).ToList();
        if (others.Count > 0)
            bands.Add(new ChartBand("Others", AccountColors.Others, months.Select(m => others.Sum(p => p.Values.GetValueOrDefault(m))).ToList(),
                OthersKey, othersHidden));
        return bands;
    }

    // Switches a band of the overview charts off or on again (the legend), remembered for the person.
    public async Task SetHiddenAsync(string userId, string key, bool hidden, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (key == OthersKey)
        {
            var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            if (s is null)
                db.UserSettings.Add(s = new UserSettings { UserId = userId });
            s.OthersHidden = hidden;
        }
        else if (key.Length > 1 && int.TryParse(key[1..], out var id) && key[0] is 'a' or 'p')
        {
            var changed = key[0] == 'a'
                ? await db.Accounts.Where(a => a.Id == id && a.UserId == userId).ExecuteUpdateAsync(x => x.SetProperty(a => a.HiddenInTotals, hidden), ct)
                : await db.Portfolios.Where(p => p.Id == id && p.UserId == userId).ExecuteUpdateAsync(x => x.SetProperty(p => p.HiddenInTotals, hidden), ct);
            if (changed == 0)
                throw new NotFoundException("That account no longer exists.");
        }
        await db.SaveChangesAsync(ct);
    }

    // The period of the overview charts, remembered for the person.
    public async Task SetMonthsAsync(string userId, int months, CancellationToken ct = default)
    {
        if (!Periods.Contains(months))
            months = DefaultMonths;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null)
            db.UserSettings.Add(s = new UserSettings { UserId = userId });
        s.OverviewMonths = months;
        await db.SaveChangesAsync(ct);
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

    public async Task DismissAsync(string userId, NoticeKind kind, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.Notices.Where(n => n.UserId == userId && n.Kind == kind).ExecuteDeleteAsync(ct);
    }

    // This month's money in and out up to today, per currency (the homepage widget).
    // Total: all of it converted into the main currency (currencies without a rate left out), when one is set.
    public async Task<(DateOnly Today, IReadOnlyList<MonthFlow> Flows, MonthFlow? Total)> MonthFlowAsync(string userId, CancellationToken ct = default)
    {
        var today = await clock.TodayAsync(userId, ct);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var moves = await db.Positions.AsNoTracking()
            .Where(p => p.Account!.UserId == userId && p.Date >= monthStart && p.Date <= today)
            .Select(p => new { p.Account!.Currency, p.Amount }).ToListAsync(ct);
        var currencies = await db.Accounts.AsNoTracking().Where(a => a.UserId == userId).Select(a => a.Currency).Distinct().ToListAsync(ct);
        var flows = currencies.Order().Select(c => new MonthFlow(c,
            moves.Where(m => m.Currency == c && m.Amount > 0).Sum(m => m.Amount),
            moves.Where(m => m.Currency == c && m.Amount < 0).Sum(m => m.Amount))).ToList();
        var table = await rates.TableAsync(userId, ct);
        var total = table is null ? null : new MonthFlow(table.MainCurrency,
            flows.Sum(f => table.Convert(f.In, f.Currency) ?? 0), flows.Sum(f => table.Convert(f.Out, f.Currency) ?? 0));
        return (today, flows, total);
    }
}
