using System.Collections.Concurrent;
using LaLira.Core.Data;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Prices;
using LaLira.Core.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace LaLira.Core.Services;

public sealed record PriceStatus(SecretStatus Key, DateTime? CheckedAt, string? Error);

// Current prices for the portfolios, fetched with each person's own EODHD key: once a day from
// 06:00 their time (the previous trading day's close), right away for a new security, and on
// request. One call per ISIN; the free plan allows 20 a day.
public sealed class PriceService(
    IDbContextFactory<AppDbContext> dbFactory, IDataProtectionProvider dataProtection, IPriceSource source,
    TimeProvider time, ILogger<PriceService> logger)
{
    public const int MaxKeyLength = 200;
    public static readonly TimeOnly DailyRefreshAt = new(6, 0);

    // The messages stored as PriceError, in English and translated where they're shown.
    public const string BadKeyMessage = "The price service didn't accept your API key. Check it in Settings.";
    public const string LimitMessage = "The price service's daily limit is used up. Prices are fetched again tomorrow.";
    public const string UnavailableMessage = "The price service couldn't be reached. Prices are fetched again tomorrow.";

    private readonly SecretProtector _protector = new(dataProtection, "LaLira.PriceApiKey.v1");
    private readonly SemaphoreSlim _refreshing = new(1, 1);

    // Search results per person and search text, kept for a while: the same search again costs
    // no call, and adding a security picked from them takes its price from here.
    private static readonly TimeSpan SearchCacheTime = TimeSpan.FromHours(6);
    private readonly ConcurrentDictionary<(string UserId, string Query), (DateTime At, IReadOnlyList<SecurityMatch> Matches)> _searches = new();

    public const int MinSearchLength = 2;
    public const int MaxSearchLength = 60;

    // Securities by name, ticker or ISIN ("ubs", "CH0244767585"), for filling in a purchase.
    // One call per new search text.
    public async Task<IReadOnlyList<SecurityMatch>> SearchAsync(string userId, string query, CancellationToken ct = default)
    {
        query = string.Join(' ', query.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (query.Length < MinSearchLength)
            throw new InputException("Type at least two letters to search.");
        if (query.Length > MaxSearchLength)
            query = query[..MaxSearchLength];
        var cacheKey = (userId, query.ToLowerInvariant());
        var now = time.GetUtcNow().UtcDateTime;
        if (_searches.TryGetValue(cacheKey, out var hit) && now - hit.At < SearchCacheTime)
            return hit.Matches;

        string apiKey;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
            var key = _protector.Read(s?.PriceApiKeyProtected);
            if (key.Status != SecretStatus.Set)
                throw new InputException("Add your EODHD API key in Settings to search.");
            apiKey = key.Value!;
        }
        try
        {
            var matches = await source.SearchAsync(apiKey, query, ct);
            _searches[cacheKey] = (now, matches);
            return matches;
        }
        catch (PriceSourceException ex)
        {
            throw new InputException(Message(ex.Problem));
        }
    }

    // Quotes for an ISIN from a recent search, if there was one.
    private IReadOnlyList<PriceQuote>? FromSearches(string userId, string isin)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var quotes = _searches.Where(e => e.Key.UserId == userId && now - e.Value.At < SearchCacheTime)
            .SelectMany(e => e.Value.Matches).Where(m => m.Isin == isin).Select(m => m.Quote).OfType<PriceQuote>().ToList();
        return quotes.Count > 0 ? quotes : null;
    }

    private static string Message(PriceProblem problem) => problem switch
    {
        PriceProblem.BadKey => BadKeyMessage,
        PriceProblem.LimitReached => LimitMessage,
        _ => UnavailableMessage,
    };

    public async Task<PriceStatus> GetStatusAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct);
        return new PriceStatus(_protector.Read(s?.PriceApiKeyProtected).Status, s?.PricesCheckedAt, s?.PriceError);
    }

    // Saves the key (empty removes it). Prices are fetched again on the next refresh.
    public async Task SetKeyAsync(string userId, string? key, CancellationToken ct = default)
    {
        key = key?.Trim() ?? "";
        if (key.Length > MaxKeyLength || key.Any(char.IsWhiteSpace))
            throw new InputException("That doesn't look like an API key. Copy it again from your EODHD dashboard.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null)
        {
            s = new UserSettings { UserId = userId };
            db.UserSettings.Add(s);
        }
        s.PriceApiKeyProtected = key.Length == 0 ? null : _protector.Protect(key);
        s.PricesCheckedAt = null;
        s.PriceError = null;
        await db.SaveChangesAsync(ct);
    }

    // Fetches the prices of everything the person still holds, or of one ISIN only. Returns how
    // many securities got a price; a problem is stored and shown with the portfolio.
    public async Task<int> RefreshAsync(string userId, string? onlyIsin = null, CancellationToken ct = default)
    {
        await _refreshing.WaitAsync(ct);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(ct);
            var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
            var key = _protector.Read(s?.PriceApiKeyProtected);
            if (s is null || key.Status != SecretStatus.Set)
                return 0;

            var wanted = await db.Holdings.AsNoTracking()
                .Where(h => h.Portfolio!.UserId == userId && h.SellDate == null && (onlyIsin == null || h.Isin == onlyIsin))
                .Select(h => new { h.Isin, h.Currency }).Distinct().ToListAsync(ct);
            var stored = await db.SecurityPrices.Where(p => p.UserId == userId).ToListAsync(ct);
            var updated = 0;
            string? error = null;
            foreach (var group in wanted.GroupBy(w => w.Isin))
            {
                IReadOnlyList<PriceQuote> quotes;
                try
                {
                    // A security just picked from a search already has its price.
                    quotes = (onlyIsin is null ? null : FromSearches(userId, group.Key))
                             ?? await source.LookupAsync(key.Value!, group.Key, ct);
                }
                catch (PriceSourceException ex)
                {
                    error = Message(ex.Problem);
                    logger.LogInformation("Price lookup stopped for user {UserId}: {Problem}", userId, ex.Problem);
                    break;
                }

                foreach (var currency in group.Select(g => g.Currency))
                {
                    if (Pick(quotes, currency) is not { } q)
                        continue;
                    var row = stored.FirstOrDefault(p => p.Isin == group.Key && p.Currency == currency);
                    if (row is null)
                    {
                        row = new SecurityPrice { UserId = userId, Isin = group.Key, Currency = currency };
                        db.SecurityPrices.Add(row);
                        stored.Add(row);
                    }
                    row.Price = q.Price;
                    row.PriceDate = q.PriceDate;
                    row.Exchange = q.Exchange;
                    row.Ticker = q.Ticker;
                    row.FetchedAt = time.GetUtcNow().UtcDateTime;
                    updated++;
                }
            }

            // A one-ISIN lookup (a new security) doesn't count as the daily refresh.
            if (onlyIsin is null)
                s.PricesCheckedAt = time.GetUtcNow().UtcDateTime;
            if (onlyIsin is null || error is not null)
                s.PriceError = error;
            await db.SaveChangesAsync(ct);
            return updated;
        }
        finally
        {
            _refreshing.Release();
        }
    }

    // The scheduler's job: each person with a key and something held, once a day from 06:00
    // their time. A failed refresh waits for the next day, so it doesn't use up the limit.
    public async Task<int> RefreshDueAsync(CancellationToken ct = default)
    {
        List<(string UserId, DateTime? CheckedAt, string TimeZone)> people;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            people = (await db.UserSettings.AsNoTracking().Where(s => s.PriceApiKeyProtected != null)
                    .Where(s => db.Holdings.Any(h => h.Portfolio!.UserId == s.UserId && h.SellDate == null))
                    .Select(s => new { s.UserId, s.PricesCheckedAt, s.TimeZone }).ToListAsync(ct))
                .Select(x => (x.UserId, x.PricesCheckedAt, x.TimeZone)).ToList();
        }
        var now = time.GetUtcNow().UtcDateTime;
        var done = 0;
        foreach (var (userId, checkedAt, timeZone) in people)
        {
            if (!IsDue(now, checkedAt, timeZone))
                continue;
            try
            {
                await RefreshAsync(userId, null, ct);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Refreshing prices failed for user {UserId}", userId);
            }
        }
        return done;
    }

    // Due when it's past 06:00 local time and the last refresh was before today's 06:00.
    internal static bool IsDue(DateTime utcNow, DateTime? checkedAtUtc, string timeZone)
    {
        var local = Regions.LocalNow(utcNow, timeZone);
        if (TimeOnly.FromDateTime(local) < DailyRefreshAt)
            return false;
        if (checkedAtUtc is not { } at)
            return true;
        return Regions.LocalNow(at, timeZone) < local.Date + DailyRefreshAt.ToTimeSpan();
    }

    // The listing in the holding's currency with the newest close, the main listing first.
    internal static PriceQuote? Pick(IEnumerable<PriceQuote> quotes, string currency) =>
        quotes.Where(q => q.Currency == currency)
            .OrderByDescending(q => q.PriceDate).ThenByDescending(q => q.IsPrimary).FirstOrDefault();
}
