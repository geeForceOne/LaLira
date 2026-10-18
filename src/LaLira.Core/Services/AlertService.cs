using LaLira.Core.Data;
using LaLira.Core.Email;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LaLira.Core.Services;

public sealed record AlertInput(AlertKind Kind, bool Above, decimal Limit);

// One alert with where its security stands now: Current is the watched figure (price, gain in
// percent or value), null without a price or with nothing held.
public sealed record AlertView(int Id, string Isin, string Currency, AlertKind Kind, bool Above, decimal Limit, bool Armed, DateTime? LastFiredAt, decimal? Current);

// All held purchases of one security in a portfolio, at the last price.
public sealed record SecurityPosition(string Name, string? Ticker, string Isin, string Currency, decimal Quantity, long Cost, decimal? Price, DateOnly? PriceDate)
{
    public long? Value => Price is { } p ? Decimals.Total(Quantity, p) : null;
    public long? Gain => Value is { } v ? v - Cost : null;
    public decimal? GainPercent => Gain is { } g && Cost != 0 ? Math.Round(g * 100m / Cost, 2) : null;

    public decimal? Figure(AlertKind kind) => kind switch
    {
        AlertKind.Price => Price,
        AlertKind.GainPercent => GainPercent,
        _ => Value is { } v ? Money.ToDecimal(v) : null,
    };
}

// A reached alert, for the email.
public sealed record FiredAlert(string PortfolioName, int PortfolioId, SecurityPosition Security, AlertKind Kind, bool Above, decimal Limit);

// Price alerts on the securities in a portfolio. They're checked after each daily price update:
// one that's reached goes out in one email with the others reached then, and waits until the
// figure is back on the other side before it can fire again.
public sealed class AlertService(
    IDbContextFactory<AppDbContext> dbFactory, SettingsService settings, EmailSender sender,
    EmailSettingsService emailSettings, ILogger<AlertService> logger)
{
    public const int MaxPerSecurity = 10;

    public async Task<IReadOnlyList<AlertView>> ListAsync(string userId, int portfolioId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var alerts = await db.PriceAlerts.AsNoTracking().Where(a => a.PortfolioId == portfolioId && a.Portfolio!.UserId == userId)
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id).ToListAsync(ct);
        var positions = await PositionsAsync(db, userId, portfolioId, ct);
        return alerts.Select(a => View(a, positions.GetValueOrDefault((a.Isin, a.Currency)))).ToList();
    }

    public async Task AddAsync(string userId, int portfolioId, string isin, string currency, AlertInput input, CancellationToken ct = default)
    {
        if (input.Limit is <= 0 && input.Kind != AlertKind.GainPercent || Math.Abs(input.Limit) > Decimals.MaxValue)
            throw new InputException(input.Kind == AlertKind.Price ? "Enter the price per share, like 87.50." : "Enter the limit as a number, like 10000.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (!await db.Portfolios.AnyAsync(p => p.Id == portfolioId && p.UserId == userId, ct))
            throw new NotFoundException("That portfolio no longer exists.");
        if (await db.PriceAlerts.CountAsync(a => a.PortfolioId == portfolioId && a.Isin == isin && a.Currency == currency, ct) >= MaxPerSecurity)
            throw new InputException("A security can have up to {0} alerts.", MaxPerSecurity);
        var position = (await PositionsAsync(db, userId, portfolioId, ct)).GetValueOrDefault((isin, currency));
        // Already reached when it's set: it waits until the figure is back on the other side.
        var reached = position?.Figure(input.Kind) is { } now && Reached(now, input.Above, input.Limit);
        db.PriceAlerts.Add(new PriceAlert
        {
            PortfolioId = portfolioId, Isin = isin, Currency = currency, Kind = input.Kind, Above = input.Above, Limit = input.Limit, Armed = !reached,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task DeleteAsync(string userId, int alertId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.PriceAlerts.Where(a => a.Id == alertId && a.Portfolio!.UserId == userId).ExecuteDeleteAsync(ct) == 0)
            throw new NotFoundException("That alert no longer exists.");
    }

    // The scheduler's job, after the price update: each person whose prices were updated since
    // their alerts were last checked. Returns how many alerts fired.
    public async Task<int> CheckDueAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var people = await db.UserSettings
            .Where(s => s.PricesCheckedAt != null && (s.AlertsCheckedAt == null || s.AlertsCheckedAt < s.PricesCheckedAt))
            .ToListAsync(ct);
        var fired = 0;
        foreach (var person in people)
        {
            var alerts = await db.PriceAlerts.Include(a => a.Portfolio).Where(a => a.Portfolio!.UserId == person.UserId).ToListAsync(ct);
            var reached = new List<FiredAlert>();
            foreach (var group in alerts.GroupBy(a => a.PortfolioId))
            {
                var positions = await PositionsAsync(db, person.UserId, group.Key, ct);
                foreach (var a in group)
                {
                    // Nothing held (all sold) or no price: nothing to check.
                    if (positions.GetValueOrDefault((a.Isin, a.Currency)) is not { } position || position.Figure(a.Kind) is not { } now)
                        continue;
                    var isReached = Reached(now, a.Above, a.Limit);
                    if (isReached && a.Armed)
                    {
                        a.Armed = false;
                        a.LastFiredAt = DateTime.UtcNow;
                        reached.Add(new FiredAlert(a.Portfolio!.Name, a.PortfolioId, position, a.Kind, a.Above, a.Limit));
                    }
                    else if (!isReached && !a.Armed)
                        a.Armed = true;
                }
            }
            person.AlertsCheckedAt = person.PricesCheckedAt;
            if (reached.Count > 0)
            {
                db.Notices.Add(new Notice { UserId = person.UserId, Kind = NoticeKind.PriceAlert, Count = reached.Count });
                await SendAsync(db, person.UserId, reached, ct);
                fired += reached.Count;
            }
            await db.SaveChangesAsync(ct);
        }
        return fired;
    }

    // The email is a courtesy: the notice on the overview shows either way.
    private async Task SendAsync(AppDbContext db, string userId, IReadOnlyList<FiredAlert> reached, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (string.IsNullOrEmpty(user?.Email) || !await emailSettings.IsConfiguredAsync(ct))
            return;
        var s = await settings.GetAsync(userId, ct);
        var t = new Texts(s.Language, Regions.Culture(s.RegionFormat));
        var publicUrl = (await emailSettings.GetAsync(ct)).PublicUrl;
        try
        {
            await sender.SendAsync(user.Email, user.ShownName, AlertEmail.Build(t, user.ShownName, reached, publicUrl), null, ct);
            logger.LogInformation("Sent {Count} price alerts to user {UserId}", reached.Count, userId);
        }
        catch (Exception ex) when (ex is EmailException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Sending price alerts to user {UserId} failed", userId);
        }
    }

    internal static bool Reached(decimal now, bool above, decimal limit) => above ? now >= limit : now <= limit;

    // The held securities of a portfolio, all purchases of each together.
    private static async Task<Dictionary<(string Isin, string Currency), SecurityPosition>> PositionsAsync(AppDbContext db, string userId, int portfolioId, CancellationToken ct)
    {
        var held = await db.Holdings.AsNoTracking().Where(h => h.PortfolioId == portfolioId && h.Portfolio!.UserId == userId && h.SellDate == null).ToListAsync(ct);
        var prices = await db.SecurityPrices.AsNoTracking().Where(p => p.UserId == userId).ToListAsync(ct);
        return held.GroupBy(h => (h.Isin, h.Currency)).ToDictionary(g => g.Key, g =>
        {
            var price = prices.FirstOrDefault(p => p.Isin == g.Key.Isin && p.Currency == g.Key.Currency);
            var first = g.OrderByDescending(h => h.BuyDate).First();
            return new SecurityPosition(first.Name, g.Select(h => h.Ticker).FirstOrDefault(x => x is not null) ?? price?.Ticker, g.Key.Isin, g.Key.Currency,
                g.Sum(h => h.Quantity), g.Sum(h => Decimals.Total(h.Quantity, h.BuyPrice)), price?.Price, price?.PriceDate);
        });
    }

    private static AlertView View(PriceAlert a, SecurityPosition? position) =>
        new(a.Id, a.Isin, a.Currency, a.Kind, a.Above, a.Limit, a.Armed, a.LastFiredAt, position?.Figure(a.Kind));
}
