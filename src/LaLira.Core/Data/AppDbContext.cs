using LaLira.Core.Models;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace LaLira.Core.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : IdentityDbContext<AppUser>(options)
{
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Position> Positions => Set<Position>();
    public DbSet<RecurringRule> RecurringRules => Set<RecurringRule>();
    public DbSet<EmailReport> EmailReports => Set<EmailReport>();
    public DbSet<Notice> Notices => Set<Notice>();
    public DbSet<EmailSettings> EmailSettings => Set<EmailSettings>();
    public DbSet<SiteSettings> SiteSettings => Set<SiteSettings>();
    public DbSet<AccountRequest> AccountRequests => Set<AccountRequest>();
    public DbSet<Portfolio> Portfolios => Set<Portfolio>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<SecurityPrice> SecurityPrices => Set<SecurityPrice>();
    public DbSet<SecurityMonthPrice> SecurityMonthPrices => Set<SecurityMonthPrice>();
    public DbSet<PriceAlert> PriceAlerts => Set<PriceAlert>();
    public DbSet<ExchangeRate> ExchangeRates => Set<ExchangeRate>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.HasOne<AppUser>().WithOne().HasForeignKey<UserSettings>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Language).HasMaxLength(8);
            e.Property(x => x.RegionFormat).HasMaxLength(16);
            e.Property(x => x.TimeZone).HasMaxLength(64);
            e.Property(x => x.MainCurrency).HasMaxLength(3);
            e.HasIndex(x => x.HomepageTokenHash);
        });

        b.Entity<Account>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(Account.MaxNameLength);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Icon).HasMaxLength(16).HasDefaultValue(AccountIcons.Default);
            e.Property(x => x.Iban).HasMaxLength(Account.MaxIbanLength);
            e.Property(x => x.Color).HasMaxLength(16);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Position>(e =>
        {
            e.HasOne(x => x.Account).WithMany(a => a.Positions).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            // Deleting a rule keeps what it booked.
            e.HasOne<RecurringRule>().WithMany().HasForeignKey(x => x.RecurringRuleId).OnDelete(DeleteBehavior.SetNull);
            e.Property(x => x.Description).HasMaxLength(Position.MaxDescriptionLength);
            e.Property(x => x.Note).HasMaxLength(Position.MaxNoteLength);
            e.HasIndex(x => new { x.AccountId, x.Date });
            e.HasIndex(x => new { x.RecurringRuleId, x.OccurrenceDate }).IsUnique();
        });

        b.Entity<RecurringRule>(e =>
        {
            e.HasOne(x => x.Account).WithMany(a => a.RecurringRules).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.DescriptionTemplate).HasMaxLength(Position.MaxDescriptionLength);
            e.Property(x => x.Language).HasMaxLength(8);
            e.HasIndex(x => x.NextDue);
        });

        b.Entity<Portfolio>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(Portfolio.MaxNameLength);
            e.Property(x => x.Color).HasMaxLength(16);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Holding>(e =>
        {
            e.HasOne(x => x.Portfolio).WithMany(p => p.Holdings).HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Name).HasMaxLength(Holding.MaxNameLength);
            e.Property(x => x.Isin).HasMaxLength(12);
            e.Property(x => x.Valor).HasMaxLength(Holding.MaxValorLength);
            e.Property(x => x.Ticker).HasMaxLength(Holding.MaxTickerLength);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => x.PortfolioId);
        });

        b.Entity<SecurityPrice>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Isin).HasMaxLength(12);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.Property(x => x.Exchange).HasMaxLength(16);
            e.Property(x => x.Ticker).HasMaxLength(32);
            e.HasIndex(x => new { x.UserId, x.Isin, x.Currency }).IsUnique();
        });

        b.Entity<SecurityMonthPrice>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Isin).HasMaxLength(12);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => new { x.UserId, x.Isin, x.Currency, x.Month }).IsUnique();
        });

        b.Entity<PriceAlert>(e =>
        {
            e.HasOne(x => x.Portfolio).WithMany().HasForeignKey(x => x.PortfolioId).OnDelete(DeleteBehavior.Cascade);
            e.Property(x => x.Isin).HasMaxLength(12);
            e.Property(x => x.Currency).HasMaxLength(3);
            e.HasIndex(x => new { x.PortfolioId, x.Isin });
        });

        b.Entity<ExchangeRate>(e =>
        {
            e.Property(x => x.From).HasMaxLength(3);
            e.Property(x => x.To).HasMaxLength(3);
            e.HasIndex(x => new { x.From, x.To, x.Month }).IsUnique();
        });

        b.Entity<EmailReport>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<Notice>(e =>
        {
            e.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.UserId);
        });

        b.Entity<SiteSettings>(e => e.Property(x => x.Id).ValueGeneratedNever());

        b.Entity<AccountRequest>(e =>
        {
            e.Property(x => x.Email).HasMaxLength(AccountRequest.MaxEmailLength);
            e.Property(x => x.Name).HasMaxLength(AppUser.MaxDisplayNameLength);
            e.Property(x => x.Message).HasMaxLength(AccountRequest.MaxMessageLength);
        });
    }
}
