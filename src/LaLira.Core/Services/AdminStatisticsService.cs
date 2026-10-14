using LaLira.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Services;

// Counts only, never amounts or names: what the admin sees about each person on the Users page.
public sealed record UserUsage(string UserId, DateTime? LastActiveAt, int Accounts, int Positions, int Recurring, int Reports);

public sealed class AdminStatisticsService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<IReadOnlyDictionary<string, UserUsage>> GetUsageAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var users = await db.Users.AsNoTracking().Select(u => new { u.Id, u.LastActiveAt }).ToListAsync(ct);
        var accounts = await db.Accounts.GroupBy(a => a.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var positions = await db.Positions.GroupBy(p => p.Account!.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var rules = await db.RecurringRules.GroupBy(r => r.Account!.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var reports = await db.EmailReports.GroupBy(r => r.UserId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return users.ToDictionary(u => u.Id, u => new UserUsage(u.Id, u.LastActiveAt,
            accounts.GetValueOrDefault(u.Id), positions.GetValueOrDefault(u.Id), rules.GetValueOrDefault(u.Id), reports.GetValueOrDefault(u.Id)));
    }
}
