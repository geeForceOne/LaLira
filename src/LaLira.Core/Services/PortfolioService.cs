using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

// Color: an AccountColors key.
public sealed record PortfolioInfo(int Id, string Name, int SortOrder, bool InOthers = false, bool HiddenInTotals = false, bool ShowInMainCurrency = false,
    string Color = "forest");

// A portfolio's name and how it shows (the Edit portfolio dialog); without a Color it gets the next free one.
public sealed record PortfolioInput(string Name, string? Color = null, bool InOthers = false);

public sealed record HoldingInput(
    string Name, string? Isin, string? Valor, string Currency, DateOnly BuyDate, decimal Quantity, decimal BuyPrice,
    DateOnly? SellDate = null, decimal? SellPrice = null, string? Ticker = null);

public sealed record SaleInput(DateOnly Date, decimal Quantity, decimal Price);

// One row of a portfolio. Totals are in cents. For a held row, Value comes from the last price
// (null without one); for a sold row it's what the sale brought in.
public sealed record HoldingView(
    int Id, string Name, string Isin, string? Valor, string Currency, DateOnly BuyDate, decimal Quantity, decimal BuyPrice,
    DateOnly? SellDate, decimal? SellPrice, decimal? Price, DateOnly? PriceDate, string? Ticker = null, string? PriceTicker = null)
{
    public bool IsSold => SellDate is not null;

    // The row's own ticker, else the one of the listing its prices come from.
    public string? ShownTicker => Ticker ?? PriceTicker;
    public long BuyTotal => Decimals.Total(Quantity, BuyPrice);
    public long? SellTotal => SellPrice is { } p ? Decimals.Total(Quantity, p) : null;
    public long? Value => IsSold ? SellTotal : Price is { } p ? Decimals.Total(Quantity, p) : null;
    public long? Gain => Value is { } v ? v - BuyTotal : null;
    public decimal? GainPercent => Gain is { } g && BuyTotal != 0 ? Math.Round(g * 100m / BuyTotal, 2) : null;
}

// The figures of one currency: what's held (cost, value from the last prices, unrealized gain)
// and what was sold (realized gain). Rows without a price aren't in Value or Unrealized.
public sealed record PortfolioTotal(string Currency, long Cost, long Value, long Unrealized, long PricedCost, long Realized, int HeldCount, int WithoutPrice)
{
    public decimal? UnrealizedPercent => PricedCost != 0 ? Math.Round(Unrealized * 100m / PricedCost, 2) : null;
}

// All currencies' totals together in the main currency, at the newest rates. Complete: every held
// row has a price and every currency a rate.
public sealed record PortfolioMainTotal(string Currency, long Value, long Cost, long Unrealized, long PricedCost, long Realized, bool Complete, DateOnly? RatesDate)
{
    public decimal? UnrealizedPercent => PricedCost != 0 ? Math.Round(Unrealized * 100m / PricedCost, 2) : null;
}

// What was held at the end of a month, at that month's close, and what it had cost. Priced: a
// held row had a close then (or nothing was held), so the value is more than just the cost.
public sealed record PortfolioMonth(DateOnly Month, long Value, long Cost, bool Priced);

// What was bought in a year (at cost) and what sales gained or lost.
public sealed record YearFlow(int Year, long Bought, long Realized);

public sealed record YearGain(int Year, long Gain, decimal? Percent);

// A portfolio's value over time in one currency, from the first purchase to this month. Rows
// without a close for a month count at what they cost.
public sealed record PortfolioHistory(string Currency, IReadOnlyList<PortfolioMonth> Months, IReadOnlyList<YearFlow> Flows)
{
    // Each year's gain: the change of the unrealized gain plus what sales realized, in percent of
    // what was there at the start plus what was bought in the year. Years before the price history
    // reaches back are left out.
    public IReadOnlyList<YearGain> Years => Months.GroupBy(m => m.Month.Year).Where(g => g.Any(m => m.Priced)).Select(g =>
    {
        var end = g.Last();
        var start = Months.LastOrDefault(m => m.Month.Year < g.Key);
        var flow = Flows.FirstOrDefault(f => f.Year == g.Key);
        var gain = end.Value - end.Cost - (start is null ? 0 : start.Value - start.Cost) + (flow?.Realized ?? 0);
        var basis = (start?.Value ?? 0) + (flow?.Bought ?? 0);
        return new YearGain(g.Key, gain, basis > 0 ? Math.Round(gain * 100m / basis, 1) : null);
    }).ToList();
}

public sealed record PortfolioView(PortfolioInfo Portfolio, IReadOnlyList<HoldingView> Holdings, IReadOnlyList<PortfolioTotal> Totals, DateOnly Today,
    IReadOnlyList<PortfolioHistory>? History = null, PortfolioMainTotal? Main = null, PortfolioHistory? MainHistory = null)
{
    // Something held in another currency than the main one, so the totals can be shown in it.
    public bool CanShowMain => Main is not null && Totals.Any(t => t.HeldCount > 0 && t.Currency != Main.Currency);

    // Totals once in the main currency instead of per currency, as the portfolio asks for.
    public bool ShowMain => CanShowMain && Portfolio.ShowInMainCurrency;
}

public sealed record PortfolioCard(PortfolioInfo Portfolio, IReadOnlyList<PortfolioTotal> Totals, int HeldCount, PortfolioMainTotal? Main = null)
{
    // One line in the main currency instead of one per currency, as the portfolio asks for.
    public bool ShowMain => Main is not null && Portfolio.ShowInMainCurrency && Totals.Any(t => t.HeldCount > 0 && t.Currency != Main.Currency);
}

// Portfolios and their holdings. Every call is scoped to the person who owns them.
public sealed class PortfolioService(IDbContextFactory<AppDbContext> dbFactory, UserClock clock, ExchangeRateService? rates = null)
{
    public async Task<IReadOnlyList<PortfolioInfo>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureColorsAsync(db, userId, ct);
        var list = await db.Portfolios.AsNoTracking().Where(p => p.UserId == userId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        return list.Select(Info).ToList();
    }

    // Every portfolio with its totals, for the overview.
    public async Task<IReadOnlyList<PortfolioCard>> CardsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureColorsAsync(db, userId, ct);
        var portfolios = await db.Portfolios.AsNoTracking().Where(p => p.UserId == userId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name).Include(p => p.Holdings).ToListAsync(ct);
        var prices = await PricesAsync(db, userId, ct);
        var table = rates is null ? null : await rates.TableAsync(userId, ct);
        return portfolios.Select(p =>
        {
            var rows = p.Holdings.Select(h => View(h, prices)).ToList();
            var totals = Totals(rows);
            return new PortfolioCard(Info(p), totals, rows.Count(r => !r.IsSold), table is null ? null : InMain(totals, table));
        }).ToList();
    }

    public async Task<PortfolioView> GetAsync(string userId, int portfolioId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureColorsAsync(db, userId, ct);
        var p = await db.Portfolios.AsNoTracking().Include(x => x.Holdings)
            .FirstOrDefaultAsync(x => x.Id == portfolioId && x.UserId == userId, ct)
            ?? throw new NotFoundException("That portfolio no longer exists.");
        var prices = await PricesAsync(db, userId, ct);
        // Held first (newest purchase first), then sold (newest sale first).
        var rows = p.Holdings.Select(h => View(h, prices))
            .OrderBy(r => r.IsSold).ThenByDescending(r => r.SellDate).ThenByDescending(r => r.BuyDate).ThenBy(r => r.Name).ThenByDescending(r => r.Id)
            .ToList();
        var today = await clock.TodayAsync(userId, ct);
        var totals = Totals(rows);
        var history = History(p.Holdings, await MonthPricesAsync(db, userId, ct), prices, today);
        var table = rates is null ? null : await rates.TableAsync(userId, ct);
        return new PortfolioView(Info(p), rows, totals, today, history,
            table is null ? null : InMain(totals, table), table is null ? null : HistoryInMain(history, table, today));
    }

    // Every portfolio's history, for the overview's main-currency chart.
    public async Task<IReadOnlyDictionary<int, IReadOnlyList<PortfolioHistory>>> HistoriesAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var portfolios = await db.Portfolios.AsNoTracking().Where(p => p.UserId == userId).Include(p => p.Holdings).ToListAsync(ct);
        var prices = await PricesAsync(db, userId, ct);
        var months = await MonthPricesAsync(db, userId, ct);
        var today = await clock.TodayAsync(userId, ct);
        return portfolios.ToDictionary(p => p.Id, p => History(p.Holdings, months, prices, today));
    }

    // The portfolio's switches for the overview charts and its totals; null leaves one as it is.
    public async Task SetOptionsAsync(string userId, int portfolioId, bool? inOthers = null, bool? hiddenInTotals = null, bool? showInMainCurrency = null,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Portfolios.FirstOrDefaultAsync(x => x.Id == portfolioId && x.UserId == userId, ct)
                ?? throw new NotFoundException("That portfolio no longer exists.");
        p.InOthers = inOthers ?? p.InOthers;
        p.HiddenInTotals = hiddenInTotals ?? p.HiddenInTotals;
        p.ShowInMainCurrency = showInMainCurrency ?? p.ShowInMainCurrency;
        await db.SaveChangesAsync(ct);
    }

    public Task<PortfolioInfo> CreateAsync(string userId, string name, CancellationToken ct = default) =>
        CreateAsync(userId, new PortfolioInput(name), ct);

    public async Task<PortfolioInfo> CreateAsync(string userId, PortfolioInput input, CancellationToken ct = default)
    {
        var name = CleanName(input.Name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureColorsAsync(db, userId, ct);
        var order = await db.Portfolios.Where(p => p.UserId == userId).Select(p => (int?)p.SortOrder).MaxAsync(ct) ?? 0;
        var p = new Portfolio
        {
            UserId = userId, Name = name, SortOrder = order + 1, InOthers = input.InOthers,
            Color = AccountColors.IsKnown(input.Color) ? input.Color : await NextColorAsync(db, userId, ct),
        };
        db.Portfolios.Add(p);
        await db.SaveChangesAsync(ct);
        return Info(p);
    }

    // Name, color and "Others" (the Edit portfolio dialog).
    public async Task UpdateAsync(string userId, int portfolioId, PortfolioInput input, CancellationToken ct = default)
    {
        var name = CleanName(input.Name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Portfolios.FirstOrDefaultAsync(x => x.Id == portfolioId && x.UserId == userId, ct)
                ?? throw new NotFoundException("That portfolio no longer exists.");
        p.Name = name;
        p.Color = AccountColors.IsKnown(input.Color) ? input.Color : p.Color;
        p.InOthers = input.InOthers;
        await db.SaveChangesAsync(ct);
    }

    // The color a new portfolio starts with: the next one no account or portfolio uses yet.
    public async Task<string> NextColorAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await EnsureColorsAsync(db, userId, ct);
        return await NextColorAsync(db, userId, ct);
    }

    private static async Task<string> NextColorAsync(AppDbContext db, string userId, CancellationToken ct)
    {
        var used = await db.Accounts.Where(a => a.UserId == userId).Select(a => a.Color).ToListAsync(ct);
        used.AddRange(await db.Portfolios.Where(p => p.UserId == userId).Select(p => p.Color).ToListAsync(ct));
        return AccountColors.NextFromEnd(used);
    }

    // Portfolios from before colors keep the one the overview chart gave them: in their order,
    // those with purchases first, each the next color from the palette's end that no account and
    // no earlier portfolio uses.
    private static async Task EnsureColorsAsync(AppDbContext db, string userId, CancellationToken ct)
    {
        if (!await db.Portfolios.AnyAsync(p => p.UserId == userId && p.Color == null, ct))
            return;
        var used = await db.Accounts.Where(a => a.UserId == userId).Select(a => a.Color).ToListAsync(ct);
        var all = await db.Portfolios.Where(p => p.UserId == userId)
            .OrderByDescending(p => p.Holdings.Any()).ThenBy(p => p.SortOrder).ThenBy(p => p.Name).ToListAsync(ct);
        used.AddRange(all.Where(p => AccountColors.IsKnown(p.Color)).Select(p => p.Color));
        foreach (var p in all.Where(p => !AccountColors.IsKnown(p.Color)))
            used.Add(p.Color = AccountColors.NextFromEnd(used));
        await db.SaveChangesAsync(ct);
    }

    // Deletes the portfolio with all its rows.
    public async Task DeleteAsync(string userId, int portfolioId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.Portfolios.Where(p => p.Id == portfolioId && p.UserId == userId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw new NotFoundException("That portfolio no longer exists.");
    }

    public async Task<int> AddHoldingAsync(string userId, int portfolioId, HoldingInput input, CancellationToken ct = default)
    {
        var clean = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Portfolios.AnyAsync(p => p.Id == portfolioId && p.UserId == userId, ct))
            throw new NotFoundException("That portfolio no longer exists.");
        var h = new Holding { PortfolioId = portfolioId };
        Apply(h, clean);
        db.Holdings.Add(h);
        await RefetchHistoryIfEarlierAsync(db, userId, h, ct);
        await db.SaveChangesAsync(ct);
        return h.Id;
    }

    // Changes a row as typed; clearing the sale makes a sold row a held one again.
    public async Task UpdateHoldingAsync(string userId, int holdingId, HoldingInput input, CancellationToken ct = default)
    {
        var clean = Validate(input);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var h = await FindAsync(db, userId, holdingId, ct);
        Apply(h, clean);
        await RefetchHistoryIfEarlierAsync(db, userId, h, ct);
        await db.SaveChangesAsync(ct);
    }

    // A purchase from before the security's known history gets the past fetched again.
    private static async Task RefetchHistoryIfEarlierAsync(AppDbContext db, string userId, Holding h, CancellationToken ct)
    {
        var listing = await db.SecurityPrices.FirstOrDefaultAsync(p => p.UserId == userId && p.Isin == h.Isin && p.Currency == h.Currency, ct);
        if (listing?.HistoryFetchedAt is null)
            return;
        var first = await db.SecurityMonthPrices.Where(m => m.UserId == userId && m.Isin == h.Isin && m.Currency == h.Currency)
            .Select(m => (DateOnly?)m.Month).MinAsync(ct);
        if (first is null || new DateOnly(h.BuyDate.Year, h.BuyDate.Month, 1) < first)
            listing.HistoryFetchedAt = null;
    }

    public async Task DeleteHoldingAsync(string userId, int holdingId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var deleted = await db.Holdings.Where(h => h.Id == holdingId && h.Portfolio!.UserId == userId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
            throw new NotFoundException("That purchase no longer exists.");
    }

    // Sells all of a held row, or part of it: then the row splits into the sold part (a new row
    // with the same purchase) and the rest, which stays held.
    public async Task SellAsync(string userId, int holdingId, SaleInput sale, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var h = await FindAsync(db, userId, holdingId, ct);
        if (h.SellDate is not null)
            throw new InputException("That purchase is already sold.");
        if (sale.Quantity <= 0)
            throw new InputException("Enter how many were sold.");
        if (sale.Quantity > h.Quantity)
            throw new InputException("You can't sell more than you hold.");
        CheckPrice(sale.Price);
        if (sale.Date < h.BuyDate)
            throw new InputException("The sale can't be before the purchase.");

        if (sale.Quantity == h.Quantity)
        {
            h.SellDate = sale.Date;
            h.SellPrice = sale.Price;
        }
        else
        {
            h.Quantity -= sale.Quantity;
            db.Holdings.Add(new Holding
            {
                PortfolioId = h.PortfolioId,
                Name = h.Name,
                Isin = h.Isin,
                Valor = h.Valor,
                Ticker = h.Ticker,
                Currency = h.Currency,
                BuyDate = h.BuyDate,
                Quantity = sale.Quantity,
                BuyPrice = h.BuyPrice,
                SellDate = sale.Date,
                SellPrice = sale.Price,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    internal static IReadOnlyList<PortfolioTotal> Totals(IReadOnlyList<HoldingView> rows) =>
        rows.GroupBy(r => r.Currency).OrderBy(g => g.Key).Select(g =>
        {
            var held = g.Where(r => !r.IsSold).ToList();
            var priced = held.Where(r => r.Value is not null).ToList();
            return new PortfolioTotal(
                g.Key,
                held.Sum(r => r.BuyTotal),
                priced.Sum(r => r.Value!.Value),
                priced.Sum(r => r.Gain!.Value),
                priced.Sum(r => r.BuyTotal),
                g.Where(r => r.IsSold).Sum(r => r.Gain ?? 0),
                held.Count,
                held.Count - priced.Count);
        }).ToList();

    internal static HoldingInput Validate(HoldingInput input)
    {
        var name = input.Name.Trim();
        if (name.Length == 0)
            throw new InputException("Enter the name of the security.");
        if (name.Length > Holding.MaxNameLength)
            throw new InputException("Keep the name under {0} characters.", Holding.MaxNameLength);
        var isin = Isin.Normalize(input.Isin);
        if (isin.Length == 0)
            throw new InputException("Enter the ISIN, like CH0038863350.");
        if (!Isin.IsValid(isin))
            throw new InputException("That ISIN isn't valid. Check it for typos.");
        var valor = new string((input.Valor ?? "").Where(c => !char.IsWhiteSpace(c) && c is not ('\'' or '’' or '.')).ToArray());
        if (valor.Length > Holding.MaxValorLength || !valor.All(char.IsAsciiDigit))
            throw new InputException("Enter the Valor number as digits only, or leave it empty.");
        var currency = input.Currency.Trim().ToUpperInvariant();
        if (!Currencies.IsKnown(currency))
            throw new InputException("Pick a currency from the list.");
        if (input.BuyDate.Year < 1900)
            throw new InputException("Enter a date after 1900.");
        if (input.Quantity <= 0 || input.Quantity > Decimals.MaxValue)
            throw new InputException("Enter how many you bought, like 10 or 2.5.");
        CheckPrice(input.BuyPrice);
        if ((input.SellDate is null) != (input.SellPrice is null))
            throw new InputException("Enter both the sale date and the sale price, or neither.");
        if (input.SellPrice is { } sp)
            CheckPrice(sp);
        if (input.SellDate is { } sd && sd < input.BuyDate)
            throw new InputException("The sale can't be before the purchase.");
        if (Math.Abs(input.Quantity * Math.Max(input.BuyPrice, input.SellPrice ?? 0)) > Money.MaxAbsolute / Money.Scale)
            throw new InputException("That amount is too large.");
        var ticker = input.Ticker?.Trim() ?? "";
        if (ticker.Length > Holding.MaxTickerLength)
            throw new InputException("Keep the ticker under {0} characters.", Holding.MaxTickerLength);
        return input with { Name = name, Isin = isin, Valor = valor.Length == 0 ? null : valor, Currency = currency, Ticker = ticker.Length == 0 ? null : ticker };
    }

    private static void CheckPrice(decimal price)
    {
        if (price <= 0 || price > Decimals.MaxValue)
            throw new InputException("Enter the price per share, like 87.50.");
    }

    private static string CleanName(string name)
    {
        name = name.Trim();
        if (name.Length == 0)
            throw new InputException("Enter a name for the portfolio.");
        if (name.Length > Portfolio.MaxNameLength)
            throw new InputException("Keep the name under {0} characters.", Portfolio.MaxNameLength);
        return name;
    }

    private static void Apply(Holding h, HoldingInput clean)
    {
        h.Name = clean.Name;
        h.Isin = clean.Isin!;
        h.Valor = clean.Valor;
        h.Ticker = clean.Ticker;
        h.Currency = clean.Currency;
        h.BuyDate = clean.BuyDate;
        h.Quantity = clean.Quantity;
        h.BuyPrice = clean.BuyPrice;
        h.SellDate = clean.SellDate;
        h.SellPrice = clean.SellPrice;
    }

    // The figures of every currency added up in the main currency, at the newest rates.
    internal static PortfolioMainTotal InMain(IReadOnlyList<PortfolioTotal> totals, RateTable table)
    {
        long Sum(Func<PortfolioTotal, long> pick) => totals.Where(t => table.Has(t.Currency)).Sum(t => table.Convert(pick(t), t.Currency) ?? 0);
        var complete = totals.All(t => table.Has(t.Currency)) && totals.All(t => t.WithoutPrice == 0);
        return new PortfolioMainTotal(table.MainCurrency, Sum(t => t.Value), Sum(t => t.Cost), Sum(t => t.Unrealized), Sum(t => t.PricedCost),
            Sum(t => t.Realized), complete, table.LatestDate);
    }

    // The histories of all currencies as one, in the main currency: past months at their month
    // end's rate, this month at the newest. Currencies without a rate are left out.
    internal static PortfolioHistory? HistoryInMain(IReadOnlyList<PortfolioHistory> parts, RateTable table, DateOnly today)
    {
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var usable = parts.Where(h => table.Has(h.Currency)).ToList();
        if (usable.Count == 0)
            return null;
        long Convert(long cents, string currency, DateOnly month) =>
            (month >= thisMonth ? table.Convert(cents, currency) : table.Convert(cents, currency, month)) ?? 0;
        var months = usable.SelectMany(h => h.Months.Select(m => m.Month)).Distinct().Order()
            .Select(month => new PortfolioMonth(month,
                usable.Sum(h => h.Months.FirstOrDefault(m => m.Month == month) is { } m ? Convert(m.Value, h.Currency, month) : 0),
                usable.Sum(h => h.Months.FirstOrDefault(m => m.Month == month) is { } m ? Convert(m.Cost, h.Currency, month) : 0),
                usable.Any(h => h.Months.FirstOrDefault(m => m.Month == month)?.Priced == true)))
            .ToList();
        // A year's flows at the rate of its last month.
        var flows = usable.SelectMany(h => h.Flows.Select(f => (h.Currency, Flow: f))).GroupBy(x => x.Flow.Year).Select(g =>
        {
            var month = new DateOnly(g.Key, 12, 1) > thisMonth ? thisMonth : new DateOnly(g.Key, 12, 1);
            return new YearFlow(g.Key, g.Sum(x => Convert(x.Flow.Bought, x.Currency, month)), g.Sum(x => Convert(x.Flow.Realized, x.Currency, month)));
        }).OrderBy(f => f.Year).ToList();
        return new PortfolioHistory(table.MainCurrency, months, flows);
    }

    // The value at each month end per currency, from the first purchase to this month: what was
    // held then times that month's close (the newest price for this month), or its cost without one.
    internal static IReadOnlyList<PortfolioHistory> History(IEnumerable<Holding> holdings,
        IReadOnlyDictionary<(string Isin, string Currency), SortedList<DateOnly, decimal>> monthPrices,
        IReadOnlyDictionary<(string, string), SecurityPrice> prices, DateOnly today)
    {
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        return holdings.Where(h => h.BuyDate <= today).GroupBy(h => h.Currency).OrderBy(g => g.Key).Select(g =>
        {
            var rows = g.ToList();
            var first = rows.Min(h => h.BuyDate);
            var months = new List<PortfolioMonth>();
            for (var m = new DateOnly(first.Year, first.Month, 1); m <= thisMonth; m = m.AddMonths(1))
            {
                var end = m.AddMonths(1).AddDays(-1);
                if (end > today) end = today;
                long value = 0, cost = 0;
                bool held = false, priced = false;
                foreach (var h in rows.Where(h => h.BuyDate <= end && (h.SellDate is null || h.SellDate > end)))
                {
                    var close = m == thisMonth && prices.GetValueOrDefault((h.Isin, h.Currency)) is { } now ? now.Price
                        : Close(monthPrices.GetValueOrDefault((h.Isin, h.Currency)), m);
                    value += Decimals.Total(h.Quantity, close ?? h.BuyPrice);
                    cost += Decimals.Total(h.Quantity, h.BuyPrice);
                    held = true;
                    priced |= close is not null;
                }
                months.Add(new PortfolioMonth(m, value, cost, priced || !held));
            }
            var flows = rows.Select(h => h.BuyDate.Year).Concat(rows.Where(h => h.SellDate is not null).Select(h => h.SellDate!.Value.Year))
                .Distinct().Order().Select(y => new YearFlow(y,
                    rows.Where(h => h.BuyDate.Year == y).Sum(h => Decimals.Total(h.Quantity, h.BuyPrice)),
                    rows.Where(h => h.SellDate?.Year == y).Sum(h => Decimals.Total(h.Quantity, h.SellPrice!.Value) - Decimals.Total(h.Quantity, h.BuyPrice))))
                .ToList();
            return new PortfolioHistory(g.Key, months, flows);
        }).ToList();
    }

    // The close of the month, or the latest one before it.
    private static decimal? Close(SortedList<DateOnly, decimal>? closes, DateOnly month)
    {
        if (closes is null || closes.Count == 0 || closes.Keys[0] > month)
            return null;
        var i = closes.Keys.Count - 1;
        while (closes.Keys[i] > month) i--;
        return closes.Values[i];
    }

    private static async Task<Dictionary<(string Isin, string Currency), SortedList<DateOnly, decimal>>> MonthPricesAsync(
        AppDbContext db, string userId, CancellationToken ct) =>
        (await db.SecurityMonthPrices.AsNoTracking().Where(m => m.UserId == userId).ToListAsync(ct))
        .GroupBy(m => (m.Isin, m.Currency))
        .ToDictionary(g => g.Key, g => new SortedList<DateOnly, decimal>(g.ToDictionary(m => m.Month, m => m.Price)));

    private static async Task<Holding> FindAsync(AppDbContext db, string userId, int holdingId, CancellationToken ct) =>
        await db.Holdings.FirstOrDefaultAsync(h => h.Id == holdingId && h.Portfolio!.UserId == userId, ct)
        ?? throw new NotFoundException("That purchase no longer exists.");

    private static async Task<Dictionary<(string, string), SecurityPrice>> PricesAsync(AppDbContext db, string userId, CancellationToken ct) =>
        (await db.SecurityPrices.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct))
        .ToDictionary(p => (p.Isin, p.Currency));

    private static HoldingView View(Holding h, Dictionary<(string, string), SecurityPrice> prices)
    {
        var listing = prices.GetValueOrDefault((h.Isin, h.Currency));
        // A sold row's value is its sale; only the ticker comes from the price data.
        var price = h.SellDate is null ? listing : null;
        return new HoldingView(h.Id, h.Name, h.Isin, h.Valor, h.Currency, h.BuyDate, h.Quantity, h.BuyPrice,
            h.SellDate, h.SellPrice, price?.Price, price?.PriceDate, h.Ticker, listing?.Ticker);
    }

    private static PortfolioInfo Info(Portfolio p) =>
        new(p.Id, p.Name, p.SortOrder, p.InOthers, p.HiddenInTotals, p.ShowInMainCurrency, p.Color ?? AccountColors.All[^1].Key);
}
