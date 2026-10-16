using LaLira.Core.Backup;
using Microsoft.Extensions.Logging;

namespace LaLira.Core.Services;

// Everything LaLira does on its own, run once per tick by the host (every minute): recurring
// bookings, email reports, the automatic backup, portfolio prices. Each job works out what's due
// from what's stored, so a tick after downtime catches up, and a job that fails doesn't stop the others.
public sealed class Scheduler(RecurringService recurring, ReportService reports, BackupService backups, PriceService prices, ILogger<Scheduler> logger)
{
    public async Task RunOnceAsync(CancellationToken ct = default)
    {
        // Bookings first, so a report sent in the same tick includes them.
        await RunAsync("recurring bookings", () => recurring.BookDueAsync(ct));
        await RunAsync("email reports", () => reports.SendDueAsync(ct));
        await RunAsync("automatic backup", async () => await backups.RunDueAsync(ct) ? 1 : 0);
        await RunAsync("portfolio prices", () => prices.RefreshDueAsync(ct));
    }

    private async Task RunAsync(string job, Func<Task<int>> work)
    {
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled job '{Job}' failed", job);
        }
    }
}
