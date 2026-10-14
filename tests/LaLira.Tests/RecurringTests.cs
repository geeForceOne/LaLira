using LaLira.Core.Data;
using LaLira.Core.Models;
using LaLira.Core.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LaLira.Tests;

// The booking job against a real (in-memory) SQLite database.
public sealed class RecurringTests : IDisposable
{
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero));
    private readonly RecurringService _recurring;
    private readonly AccountService _accounts;

    public RecurringTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Users.Add(new AppUser { Id = "u1", UserName = "a@b.ch", Email = "a@b.ch" });
            db.UserSettings.Add(new UserSettings { UserId = "u1", Language = "de", RegionFormat = "de-CH", TimeZone = "Europe/Zurich" });
            db.SaveChanges();
        }
        var settings = new SettingsService(_factory);
        var userClock = new UserClock(_clock, settings);
        _accounts = new AccountService(_factory);
        _recurring = new RecurringService(_factory, _accounts, settings, userClock, NullLogger<RecurringService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private async Task<int> NewAccountAsync() =>
        (await _accounts.CreateAsync("u1", new AccountInput("Sparkonto", "CHF", 0, new DateOnly(2026, 1, 1), true, true, "piggy"))).Id;

    [Fact]
    public async Task BackfillsCatchesUpAndNeverBooksTwice()
    {
        var account = await NewAccountAsync();
        var input = new RuleInput(account, 25000, "Einzahlung {month}", Frequency.Monthly, 1, 1, new DateOnly(2026, 7, 1), null, "de", true);

        // Created on 14 October with a start in July: July to October are booked right away.
        var (rule, booked) = await _recurring.CreateAsync("u1", input);
        Assert.Equal(4, booked);
        Assert.Equal(new DateOnly(2026, 11, 1), rule.NextDue);

        // The app is down until 3 December: November and December are caught up with their own dates.
        _clock.Now = new DateTimeOffset(2026, 12, 3, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(2, await _recurring.BookDueAsync());
        Assert.Equal(0, await _recurring.BookDueAsync());

        await using var db = _factory.CreateDbContext();
        var positions = await db.Positions.OrderBy(p => p.Date).ToListAsync();
        Assert.Equal(6, positions.Count);
        Assert.Equal("Einzahlung Dezember", positions[^1].Description);
        Assert.Equal(new DateOnly(2026, 12, 1), positions[^1].Date);
        Assert.Equal(2, (await db.Notices.SingleAsync()).Count);
    }

    [Fact]
    public async Task BooksAfterLocalMidnight()
    {
        var account = await NewAccountAsync();
        await _recurring.CreateAsync("u1", new RuleInput(account, 1000, "Fee", Frequency.Monthly, 1, 1, new DateOnly(2026, 11, 1), null, "en", true));

        // 31 Oct 23:30 UTC is already 1 Nov 00:30 in Zurich.
        _clock.Now = new DateTimeOffset(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(1, await _recurring.BookDueAsync());
        await using var db = _factory.CreateDbContext();
        Assert.Empty(await db.Notices.ToListAsync());
    }

    [Fact]
    public async Task PausedRuleResumesFromToday()
    {
        var account = await NewAccountAsync();
        var input = new RuleInput(account, 1000, "Fee", Frequency.Monthly, 1, 1, new DateOnly(2026, 10, 1), null, "en", true);
        var (rule, _) = await _recurring.CreateAsync("u1", input);
        await _recurring.UpdateAsync("u1", rule.Id, input with { IsActive = false });

        _clock.Now = new DateTimeOffset(2027, 2, 10, 8, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, await _recurring.BookDueAsync());
        var (resumed, booked) = await _recurring.UpdateAsync("u1", rule.Id, input);
        Assert.Equal(0, booked);
        Assert.Equal(new DateOnly(2027, 3, 1), resumed.NextDue);
    }
}
