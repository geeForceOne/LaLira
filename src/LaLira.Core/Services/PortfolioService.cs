using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

public sealed record PortfolioInfo(int Id, string Name, int SortOrder);

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

public sealed record PortfolioView(PortfolioInfo Portfolio, IReadOnlyList<HoldingView> Holdings, IReadOnlyList<PortfolioTotal> Totals, DateOnly Today);

public sealed record PortfolioCard(PortfolioInfo Portfolio, IReadOnlyList<PortfolioTotal> Totals, int HeldCount);

// Portfolios and their holdings. Every call is scoped to the person who owns them.
public sealed class PortfolioService(IDbContextFactory<AppDbContext> dbFactory, UserClock clock)
{
    public async Task<IReadOnlyList<PortfolioInfo>> ListAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.Portfolios.AsNoTracking().Where(p => p.UserId == userId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name)
            .Select(p => new PortfolioInfo(p.Id, p.Name, p.SortOrder)).ToListAsync(ct);
    }

    // Every portfolio with its totals, for the overview.
    public async Task<IReadOnlyList<PortfolioCard>> CardsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var portfolios = await db.Portfolios.AsNoTracking().Where(p => p.UserId == userId)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Name).Include(p => p.Holdings).ToListAsync(ct);
        var prices = await PricesAsync(db, userId, ct);
        return portfolios.Select(p =>
        {
            var rows = p.Holdings.Select(h => View(h, prices)).ToList();
            return new PortfolioCard(Info(p), Totals(rows), rows.Count(r => !r.IsSold));
        }).ToList();
    }

    public async Task<PortfolioView> GetAsync(string userId, int portfolioId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var p = await db.Portfolios.AsNoTracking().Include(x => x.Holdings)
            .FirstOrDefaultAsync(x => x.Id == portfolioId && x.UserId == userId, ct)
            ?? throw new NotFoundException("That portfolio no longer exists.");
        var prices = await PricesAsync(db, userId, ct);
        // Held first (newest purchase first), then sold (newest sale first).
        var rows = p.Holdings.Select(h => View(h, prices))
            .OrderBy(r => r.IsSold).ThenByDescending(r => r.SellDate).ThenByDescending(r => r.BuyDate).ThenBy(r => r.Name).ThenByDescending(r => r.Id)
            .ToList();
        return new PortfolioView(Info(p), rows, Totals(rows), await clock.TodayAsync(userId, ct));
    }

    public async Task<PortfolioInfo> CreateAsync(string userId, string name, CancellationToken ct = default)
    {
        name = CleanName(name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var order = await db.Portfolios.Where(p => p.UserId == userId).Select(p => (int?)p.SortOrder).MaxAsync(ct) ?? 0;
        var p = new Portfolio { UserId = userId, Name = name, SortOrder = order + 1 };
        db.Portfolios.Add(p);
        await db.SaveChangesAsync(ct);
        return Info(p);
    }

    public async Task RenameAsync(string userId, int portfolioId, string name, CancellationToken ct = default)
    {
        name = CleanName(name);
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var changed = await db.Portfolios.Where(p => p.Id == portfolioId && p.UserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Name, name), ct);
        if (changed == 0)
            throw new NotFoundException("That portfolio no longer exists.");
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
        await db.SaveChangesAsync(ct);
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

    private static PortfolioInfo Info(Portfolio p) => new(p.Id, p.Name, p.SortOrder);
}
