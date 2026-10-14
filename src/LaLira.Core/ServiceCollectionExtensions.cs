using LaLira.Core.Backup;
using LaLira.Core.Data;
using LaLira.Core.Email;
using LaLira.Core.Localization;
using LaLira.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LaLira.Core;

public static class ServiceCollectionExtensions
{
    // Registers persistence and the Core services. The host is responsible for Data Protection
    // (key storage location), Identity, and calling Scheduler.RunOnceAsync every minute.
    public static IServiceCollection AddLaLiraCore(this IServiceCollection services, StoragePaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new Texts());
        services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite($"Data Source={paths.DatabasePath}"));
        services.AddSingleton<EmailSettingsService>();
        services.AddSingleton<EmailSender>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<UserClock>();
        services.AddSingleton<AccountService>();
        services.AddSingleton<LedgerService>();
        services.AddSingleton<RecurringService>();
        services.AddSingleton<OverviewService>();
        services.AddSingleton<ReportService>();
        services.AddSingleton<CsvService>();
        services.AddSingleton<BackupService>();
        services.AddSingleton<Scheduler>();
        services.AddSingleton<AccountRequestService>();
        services.AddSingleton<UserActivityService>();
        services.AddSingleton<AdminStatisticsService>();
        services.AddSingleton<WhatsNewService>();
        return services;
    }
}
