using System.Collections.Concurrent;
using LaLira.Core.Data;
using LaLira.Core.Models;
using LaLira.Core.Prices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LaLira.Core.Services;

// Month-end rates of some currencies in a main currency, for converting amounts (display only:
// everything is stored and booked in its own currency). A month without its own rate takes the
// closest earlier one, or the first there is.
public sealed class RateTable
{
    public static readonly RateTable Empty = new("", new Dictionary<string, SortedList<DateOnly, (decimal Rate, DateOnly Date)>>());

    private readonly IReadOnlyDictionary<string, SortedList<DateOnly, (decimal Rate, DateOnly Date)>> _rates;

    public RateTable(string mainCurrency, IReadOnlyDictionary<string, SortedList<DateOnly, (decimal Rate, DateOnly Date)>> rates)
    {
        MainCurrency = mainCurrency;
        _rates = rates;
    }

    public string MainCurrency { get; }

    public bool Has(string currency) => currency == MainCurrency || _rates.TryGetValue(currency, out var r) && r.Count > 0;

    // The day the newest rates are from (the oldest of them, when currencies differ).
    public DateOnly? LatestDate => _rates.Values.Where(r => r.Count > 0).Select(r => r.Values[^1].Date).DefaultIfEmpty().Min() is { } d && d != default ? d : null;

    // In the main currency with the newest rate; null without one.
    public long? Convert(long cents, string currency) =>
        currency == MainCurrency ? cents
        : _rates.TryGetValue(currency, out var r) && r.Count > 0 ? Apply(cents, r.Values[^1].Rate) : null;

    // With the rate of that month's end.
    public long? Convert(long cents, string currency, DateOnly month)
    {
        if (currency == MainCurrency)
            return cents;
        if (!_rates.TryGetValue(currency, out var r) || r.Count == 0)
            return null;
        var key = new DateOnly(month.Year, month.Month, 1);
        var rate = r.Values[0].Rate;
        foreach (var (m, v) in r)
        {
            if (m > key) break;
            rate = v.Rate;
        }
        return Apply(cents, rate);
    }

    private static long Apply(long cents, decimal rate) => (long)Math.Round(cents * rate, MidpointRounding.AwayFromZero);
}

// Exchange rates from Frankfurter into each person's main currency. Each currency pair is fetched
// once from the earliest month it's needed for, then kept up to date once a day (after 16:00 UTC,
// when the central banks have published); a pair with no rates yet is fetched on the next tick.
public sealed class ExchangeRateService(IDbContextFactory<AppDbContext> dbFactory, IRateSource source, TimeProvider time, ILogger<ExchangeRateService> logger)
{
    public const int DailyUpdateHourUtc = 16;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    // When each pair was last tried; failures and unknown currencies aren't asked again right away.
    private readonly ConcurrentDictionary<(string From, string To), DateTime> _tried = new();
    private readonly ConcurrentDictionary<string, DateTime> _unknown = new();
    private readonly SemaphoreSlim _updating = new(1, 1);

    // The person's main currency with the rates of everything they have in other currencies, or
    // null when it's off.
    public async Task<RateTable?> TableAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var main = await db.UserSettings.AsNoTracking().Where(s => s.UserId == userId).Select(s => s.MainCurrency).FirstOrDefaultAsync(ct);
        if (main is null)
            return null;
        var rows = await db.ExchangeRates.AsNoTracking().Where(r => r.To == main).ToListAsync(ct);
        var rates = rows.GroupBy(r => r.From).ToDictionary(g => g.Key,
            g => new SortedList<DateOnly, (decimal, DateOnly)>(g.ToDictionary(r => r.Month, r => (r.Rate, r.RateDate))));
        return new RateTable(main, rates);
    }

    // Whether a currency is one the rate service doesn't know (so its amounts can't be converted).
    public bool IsUnknown(string currency) => _unknown.ContainsKey(currency);

    // The scheduler's job, and right after someone picks a main currency. Returns how many pairs
    // got new rates.
    public async Task<int> UpdateDueAsync(CancellationToken ct = default)
    {
        await _updating.WaitAsync(ct);
        try
        {
            var now = time.GetUtcNow().UtcDateTime;
            var today = DateOnly.FromDateTime(now);
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var needed = await NeededAsync(db, today, ct);
            var stored = await db.ExchangeRates.ToListAsync(ct);
            var updated = 0;
            foreach (var ((from, to), since) in needed)
            {
                if (_unknown.TryGetValue(from, out var unknownAt) && now - unknownAt < TimeSpan.FromDays(1))
                    continue;
                var mine = stored.Where(r => r.From == from && r.To == to).OrderBy(r => r.Month).ToList();
                var start = new DateOnly(since.Year, since.Month, 1);
                DateOnly fetchFrom;
                if (mine.Count == 0 || start < mine[0].Month)
                    fetchFrom = start;
                else if (IsDue(now, mine.Max(r => r.FetchedAt)))
                    fetchFrom = mine[^1].Month;
                else
                    continue;
                if (_tried.TryGetValue((from, to), out var at) && now - at < RetryAfter)
                    continue;
                _tried[(from, to)] = now;

                IReadOnlyList<DayRate> days;
                try
                {
                    days = await source.DailyAsync(from, to, fetchFrom, today, ct);
                }
                catch (RateSourceException ex)
                {
                    if (ex.UnknownCurrency)
                        _unknown[from] = now;
                    logger.LogInformation("Exchange rates {From}/{To} not fetched: {Problem}", from, to, ex.Message);
                    continue;
                }
                foreach (var monthEnd in MonthEnds(days))
                {
                    var month = new DateOnly(monthEnd.Date.Year, monthEnd.Date.Month, 1);
                    var row = mine.FirstOrDefault(r => r.Month == month);
                    if (row is null)
                    {
                        row = new ExchangeRate { From = from, To = to, Month = month };
                        db.ExchangeRates.Add(row);
                        mine.Add(row);
                    }
                    row.Rate = monthEnd.Rate;
                    row.RateDate = monthEnd.Date;
                    row.FetchedAt = now;
                }
                // Mark the pair as checked even when there was nothing new (a holiday).
                foreach (var r in mine.Where(r => r.Month == mine.Max(x => x.Month)))
                    r.FetchedAt = now;
                updated++;
            }
            await db.SaveChangesAsync(ct);
            return updated;
        }
        finally
        {
            _updating.Release();
        }
    }

    // Each month's last rate.
    internal static IEnumerable<DayRate> MonthEnds(IEnumerable<DayRate> days) =>
        days.GroupBy(d => (d.Date.Year, d.Date.Month)).Select(g => g.MaxBy(d => d.Date)!);

    // Due once a day: after 16:00 UTC when the last update was before that.
    internal static bool IsDue(DateTime utcNow, DateTime lastFetchUtc)
    {
        var todays = utcNow.Date.AddHours(DailyUpdateHourUtc);
        return utcNow >= todays ? lastFetchUtc < todays : lastFetchUtc < todays.AddDays(-1);
    }

    // The currency pairs anyone needs, each with the earliest date: the opening of the oldest
    // account or the first purchase in that currency.
    private static async Task<Dictionary<(string From, string To), DateOnly>> NeededAsync(AppDbContext db, DateOnly today, CancellationToken ct)
    {
        var people = await db.UserSettings.AsNoTracking().Where(s => s.MainCurrency != null)
            .Select(s => new { s.UserId, Main = s.MainCurrency! }).ToListAsync(ct);
        var result = new Dictionary<(string, string), DateOnly>();
        foreach (var p in people)
        {
            var accounts = await db.Accounts.AsNoTracking().Where(a => a.UserId == p.UserId && a.Currency != p.Main)
                .GroupBy(a => a.Currency).Select(g => new { Currency = g.Key, Since = g.Min(a => a.OpeningDate) }).ToListAsync(ct);
            var holdings = await db.Holdings.AsNoTracking().Where(h => h.Portfolio!.UserId == p.UserId && h.Currency != p.Main)
                .GroupBy(h => h.Currency).Select(g => new { Currency = g.Key, Since = g.Min(h => h.BuyDate) }).ToListAsync(ct);
            var wanted = accounts.Select(a => (a.Currency, a.Since))
                .Concat(holdings.Select(h => (h.Currency, Since: h.Since > today ? today : h.Since)));
            foreach (var (currency, since) in wanted)
            {
                var key = (currency, p.Main);
                result[key] = result.TryGetValue(key, out var s) && s < since ? s : since;
            }
        }
        return result;
    }
}
