using LaLira.Core.Localization;

namespace LaLira.Core.Services;

// "Today" and "now" in each person's own time zone; the server itself runs on UTC.
public sealed class UserClock(TimeProvider time, SettingsService settings)
{
    public DateTime UtcNow => time.GetUtcNow().UtcDateTime;

    public async Task<DateOnly> TodayAsync(string userId, CancellationToken ct = default) =>
        Regions.LocalToday(UtcNow, (await settings.GetAsync(userId, ct)).TimeZone);

    public async Task<DateTime> NowAsync(string userId, CancellationToken ct = default) =>
        Regions.LocalNow(UtcNow, (await settings.GetAsync(userId, ct)).TimeZone);
}
