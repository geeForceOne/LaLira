using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using LaLira.Core.Data;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

public sealed record SettingsView(
    Theme Theme, string Language, string RegionFormat, string TimeZone, bool HasHomepageToken, DateTime? HomepageTokenCreatedAt,
    string? MainCurrency = null);

public sealed record SettingsUpdate(Theme Theme, string Language, string RegionFormat, string TimeZone);

// Each person's preferences. Read on every request (for the page's language and formats), so
// they're kept in memory and refreshed on save.
public sealed class SettingsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public const string HomepageTokenPrefix = "llr_";

    private readonly ConcurrentDictionary<string, SettingsView> _cache = new();

    public async Task<SettingsView> GetAsync(string userId, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(userId, out var cached))
            return cached;
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(x => x.UserId == userId, ct) ?? new UserSettings { UserId = userId };
        return _cache[userId] = View(s);
    }

    // Whether the person has settings yet; the first visit fills them from the browser.
    public async Task<bool> ExistsAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserSettings.AnyAsync(x => x.UserId == userId, ct);
    }

    // First visit: language, region format and time zone as the browser reports them.
    public async Task InitializeFromBrowserAsync(string userId, string? browserLanguage, string? browserTimeZone, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        if (await db.UserSettings.AnyAsync(x => x.UserId == userId, ct))
            return;
        var (language, region) = Regions.FromBrowser(browserLanguage);
        db.UserSettings.Add(new UserSettings
        {
            UserId = userId,
            Language = language,
            RegionFormat = region,
            TimeZone = Regions.IsKnownTimeZone(browserTimeZone) ? browserTimeZone! : Regions.DefaultTimeZone,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Another tab got there first.
        }
        _cache.TryRemove(userId, out _);
    }

    public async Task SaveAsync(string userId, SettingsUpdate update, CancellationToken ct = default)
    {
        if (!L.Languages.Contains(update.Language))
            throw new InputException("Pick a language from the list.");
        if (!Regions.RegionFormats.Contains(update.RegionFormat))
            throw new InputException("Pick a region format from the list.");
        if (!Regions.IsKnownTimeZone(update.TimeZone))
            throw new InputException("Pick a time zone from the list.");

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.Theme = update.Theme;
        s.Language = update.Language;
        s.RegionFormat = update.RegionFormat;
        s.TimeZone = update.TimeZone;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
    }

    public async Task SetThemeAsync(string userId, Theme theme, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.Theme = theme;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
    }

    // The currency everything is also shown in; null switches the conversion off.
    public async Task SetMainCurrencyAsync(string userId, string? currency, CancellationToken ct = default)
    {
        currency = string.IsNullOrWhiteSpace(currency) ? null : currency.Trim().ToUpperInvariant();
        if (currency is not null && !Currencies.IsKnown(currency))
            throw new InputException("Pick a currency from the list.");
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.MainCurrency = currency;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
    }

    // A new homepage widget token, replacing any earlier one. Only its hash is stored, so this is
    // the one time it can be shown.
    public async Task<string> CreateHomepageTokenAsync(string userId, CancellationToken ct = default)
    {
        var token = HomepageTokenPrefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var s = await LoadOrAddAsync(db, userId, ct);
        s.HomepageTokenHash = Hash(token);
        s.HomepageTokenCreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(ct);
        _cache.TryRemove(userId, out _);
        return token;
    }

    public async Task RevokeHomepageTokenAsync(string userId, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        await db.UserSettings.Where(x => x.UserId == userId)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.HomepageTokenHash, (string?)null).SetProperty(x => x.HomepageTokenCreatedAt, (DateTime?)null), ct);
        _cache.TryRemove(userId, out _);
    }

    // The user a homepage token belongs to, or null.
    public async Task<string?> FindUserByHomepageTokenAsync(string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || !token.StartsWith(HomepageTokenPrefix, StringComparison.Ordinal))
            return null;
        var hash = Hash(token.Trim());
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        return await db.UserSettings.AsNoTracking().Where(x => x.HomepageTokenHash == hash).Select(x => x.UserId).FirstOrDefaultAsync(ct);
    }

    public void Forget(string userId) => _cache.TryRemove(userId, out _);

    private static async Task<UserSettings> LoadOrAddAsync(AppDbContext db, string userId, CancellationToken ct)
    {
        var s = await db.UserSettings.FirstOrDefaultAsync(x => x.UserId == userId, ct);
        if (s is null)
        {
            s = new UserSettings { UserId = userId };
            db.UserSettings.Add(s);
        }
        return s;
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static SettingsView View(UserSettings s) =>
        new(s.Theme, L.Normalize(s.Language), s.RegionFormat, s.TimeZone, s.HomepageTokenHash is not null, s.HomepageTokenCreatedAt,
            s.MainCurrency);
}
