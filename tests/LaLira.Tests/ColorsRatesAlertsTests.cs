using System.Globalization;
using LaLira.Core.Data;
using LaLira.Core.Email;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Prices;
using LaLira.Core.Reports;
using LaLira.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LaLira.Tests;

// Account colors, exchange rates into a main currency, and price alerts.
public sealed class ColorsRatesAlertsTests : IDisposable
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

    // EUR in CHF: 0.95 through 2025, 0.93 from January 2026 on.
    private sealed class FakeRates : IRateSource
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DayRate>> DailyAsync(string fromCurrency, string toCurrency, DateOnly from, DateOnly to, CancellationToken ct = default)
        {
            Calls++;
            if (fromCurrency == "XXX")
                throw new RateSourceException(true);
            var days = new List<DayRate>();
            for (var d = from; d <= to; d = d.AddDays(1))
                days.Add(new DayRate(d, d.Year < 2026 ? 0.95m : 0.93m));
            return Task.FromResult<IReadOnlyList<DayRate>>(days);
        }
    }

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 17, 18, 0, 0, TimeSpan.Zero));
    private readonly FakeRates _source = new();
    private readonly AccountService _accounts;
    private readonly SettingsService _settings;
    private readonly ExchangeRateService _rates;
    private readonly OverviewService _overview;
    private readonly PortfolioService _portfolios;
    private readonly AlertService _alerts;

    public ColorsRatesAlertsTests()
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
        _settings = new SettingsService(_factory);
        var userClock = new UserClock(_clock, _settings);
        _accounts = new AccountService(_factory);
        _rates = new ExchangeRateService(_factory, _source, _clock, NullLogger<ExchangeRateService>.Instance);
        _portfolios = new PortfolioService(_factory, userClock, _rates);
        _overview = new OverviewService(_factory, _accounts, userClock, _portfolios, _rates);
        var email = new EmailSettingsService(_factory, new EphemeralDataProtectionProvider());
        _alerts = new AlertService(_factory, _settings, new EmailSender(email), email, NullLogger<AlertService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private Task<AccountInfo> AccountAsync(string name, string currency, long opening, DateOnly opened, string? color = null) =>
        _accounts.CreateAsync("u1", new AccountInput(name, currency, opening, opened, true, true, "bank", null, color));

    [Fact]
    public void NextColorIsTheFirstUnused()
    {
        Assert.Equal("blue", AccountColors.Next([]));
        Assert.Equal("green", AccountColors.Next(["blue", "teal", null, "nonsense"]));
        // All taken: they repeat in order.
        Assert.Equal("blue", AccountColors.Next(AccountColors.All.Select(c => (string?)c.Key)));
        Assert.All(AccountColors.All, c => Assert.True(L.GermanDictionary.ContainsKey(c.Label), c.Label));
    }

    [Fact]
    public async Task NewAccountsGetTheNextFreeColorAndKeepAPickedOne()
    {
        Assert.Equal("blue", (await AccountAsync("A", "CHF", 0, new DateOnly(2026, 1, 1))).Color);
        Assert.Equal("purple", (await AccountAsync("B", "CHF", 0, new DateOnly(2026, 1, 1), "purple")).Color);
        Assert.Equal("teal", (await AccountAsync("C", "CHF", 0, new DateOnly(2026, 1, 1))).Color);
        Assert.Equal("green", (await AccountAsync("D", "CHF", 0, new DateOnly(2026, 1, 1), "no-such-color")).Color);
    }

    [Fact]
    public async Task AccountsFromBeforeColorsGetOneOnFirstUse()
    {
        await using (var db = _factory.CreateDbContext())
        {
            db.Accounts.Add(new Account { UserId = "u1", Name = "Old 1", OpeningDate = new DateOnly(2020, 1, 1), SortOrder = 1 });
            db.Accounts.Add(new Account { UserId = "u1", Name = "Old 2", OpeningDate = new DateOnly(2020, 1, 1), SortOrder = 2, Color = "blue" });
            db.Accounts.Add(new Account { UserId = "u1", Name = "Old 3", OpeningDate = new DateOnly(2020, 1, 1), SortOrder = 3 });
            await db.SaveChangesAsync();
        }
        var list = await _accounts.ListAsync("u1");
        Assert.Equal(["teal", "blue", "green"], list.Select(a => a.Color));
    }

    [Fact]
    public void RateTableTakesTheMonthsRateOrTheClosestEarlierOne()
    {
        var table = new RateTable("CHF", new Dictionary<string, SortedList<DateOnly, (decimal, DateOnly)>>
        {
            ["EUR"] = new()
            {
                [new DateOnly(2026, 1, 1)] = (0.90m, new DateOnly(2026, 1, 31)),
                [new DateOnly(2026, 3, 1)] = (0.95m, new DateOnly(2026, 3, 31)),
            },
        });
        Assert.Equal(10000, table.Convert(10000, "CHF"));
        Assert.Equal(9500, table.Convert(10000, "EUR"));
        Assert.Equal(9000, table.Convert(10000, "EUR", new DateOnly(2026, 2, 15)));
        Assert.Equal(9500, table.Convert(10000, "EUR", new DateOnly(2026, 3, 1)));
        // Before the first rate: the first one.
        Assert.Equal(9000, table.Convert(10000, "EUR", new DateOnly(2019, 5, 1)));
        Assert.Null(table.Convert(10000, "USD"));
        Assert.Equal(new DateOnly(2026, 3, 31), table.LatestDate);
    }

    [Fact]
    public void FrankfurterRowsAndMonthEnds()
    {
        var rows = FrankfurterRateSource.Parse(
            [
                new FrankfurterRateSource.Row { Date = "2026-10-02", Quote = "CHF", Rate = 0.93m },
                new FrankfurterRateSource.Row { Date = "2026-09-30", Quote = "CHF", Rate = 0.94m },
                new FrankfurterRateSource.Row { Date = "2026-09-01", Quote = "CHF", Rate = 0.92m },
                new FrankfurterRateSource.Row { Date = "bad", Quote = "CHF", Rate = 1m },
                new FrankfurterRateSource.Row { Date = "2026-09-15", Quote = "USD", Rate = 1.2m },
            ], "CHF");
        Assert.Equal(3, rows.Count);
        var ends = ExchangeRateService.MonthEnds(rows).ToList();
        Assert.Equal([new DayRate(new DateOnly(2026, 9, 30), 0.94m), new DayRate(new DateOnly(2026, 10, 2), 0.93m)], ends);
    }

    [Fact]
    public void RatesAreDueOnceADayAfterFourPmUtc()
    {
        var now = new DateTime(2026, 10, 17, 17, 0, 0, DateTimeKind.Utc);
        Assert.True(ExchangeRateService.IsDue(now, new DateTime(2026, 10, 17, 15, 0, 0)));
        Assert.False(ExchangeRateService.IsDue(now, new DateTime(2026, 10, 17, 16, 30, 0)));
        var morning = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        Assert.False(ExchangeRateService.IsDue(morning, new DateTime(2026, 10, 16, 16, 30, 0)));
        Assert.True(ExchangeRateService.IsDue(morning, new DateTime(2026, 10, 16, 10, 0, 0)));
    }

    [Fact]
    public async Task OverviewStacksAccountsAndConvertsToTheMainCurrency()
    {
        var chf = await AccountAsync("Privat", "CHF", 100000, new DateOnly(2025, 11, 1));
        var eur = await AccountAsync("Euro", "EUR", 50000, new DateOnly(2025, 12, 1));
        await AccountAsync("Odd", "XXX", 1000, new DateOnly(2026, 10, 1));

        // Without a main currency: no converted total, but each currency's bands.
        var plain = await _overview.GetAsync("u1");
        Assert.Null(plain.Main);
        var chfTotal = plain.Totals.Single(t => t.Currency == "CHF");
        Assert.Equal("Privat", Assert.Single(chfTotal.Bands).Name);
        Assert.Equal(chf.Color, chfTotal.Bands[0].Color);
        Assert.Equal(chfTotal.MonthEnds.Select(m => m.Balance), chfTotal.Bands[0].Values);

        await _settings.SetMainCurrencyAsync("u1", "CHF");
        await _rates.UpdateDueAsync();
        var calls = _source.Calls;
        // Up to date: nothing is fetched again the same day.
        await _rates.UpdateDueAsync();
        Assert.Equal(calls, _source.Calls);

        var main = (await _overview.GetAsync("u1")).Main!;
        Assert.Equal("CHF", main.Currency);
        Assert.Equal(["XXX"], main.Missing);
        // 1000.00 CHF + 500.00 EUR at today's 0.93.
        Assert.Equal(100000 + 46500, main.Balance);
        Assert.Equal(new DateOnly(2025, 11, 1), main.MonthEnds[0].Month);
        // December 2025: the euro account at 0.95; November: before it was opened.
        Assert.Equal(100000 + 47500, main.MonthEnds[1].Balance);
        Assert.Equal(100000, main.MonthEnds[0].Balance);
        Assert.Equal(["Privat", "Euro"], main.Bands.Select(b => b.Name));
        Assert.Equal(eur.Color, main.Bands[1].Color);
        Assert.False(main.HasPortfolios);

        var (_, _, total) = await _overview.MonthFlowAsync("u1");
        Assert.Equal("CHF", total!.Currency);
    }

    [Fact]
    public async Task OthersAddUpIntoOneBandAndBandsCanBeHidden()
    {
        var big = await AccountAsync("Privat", "CHF", 100000, new DateOnly(2026, 1, 1));
        await _accounts.CreateAsync("u1", new AccountInput("Kasse", "CHF", 2000, new DateOnly(2026, 1, 1), true, true, "wallet", InOthers: true));
        await _accounts.CreateAsync("u1", new AccountInput("Bar", "CHF", 3000, new DateOnly(2026, 1, 1), true, true, "wallet", InOthers: true));

        var total = Assert.Single((await _overview.GetAsync("u1")).Totals);
        Assert.Equal(["Privat", "Others"], total.Bands.Select(b => b.Name));
        Assert.Equal(OverviewService.OthersKey, total.Bands[1].Key);
        Assert.Equal(5000, total.Bands[1].Values[^1]);

        await _overview.SetHiddenAsync("u1", $"a{big.Id}", true);
        await _overview.SetHiddenAsync("u1", OverviewService.OthersKey, true);
        await _overview.SetMonthsAsync("u1", 12);
        var view = await _overview.GetAsync("u1");
        Assert.All(view.Totals[0].Bands, b => Assert.True(b.Hidden));
        // Hiding is for the chart only: the total stays the real one.
        Assert.Equal(105000, view.Totals[0].Balance);
        Assert.Equal(12, view.Months);
        await _overview.SetMonthsAsync("u1", 7);
        Assert.Equal(OverviewService.DefaultMonths, (await _overview.GetAsync("u1")).Months);
    }

    [Fact]
    public async Task APortfolioInSeveralCurrenciesCanShowOnceInTheMainCurrency()
    {
        await _settings.SetMainCurrencyAsync("u1", "CHF");
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, new HoldingInput("Nestlé", "CH0038863350", null, "CHF", new DateOnly(2026, 1, 5), 10, 100m));
        await _portfolios.AddHoldingAsync("u1", p.Id, new HoldingInput("SAP", "DE0007164600", null, "EUR", new DateOnly(2026, 1, 5), 10, 100m));
        await _rates.UpdateDueAsync();

        var card = Assert.Single(await _portfolios.CardsAsync("u1"));
        Assert.False(card.ShowMain);
        // 1000 CHF and 1000 EUR at 0.93; no prices yet.
        Assert.Equal(100000 + 93000, card.Main!.Cost);
        Assert.False(card.Main.Complete);
        await _portfolios.SetOptionsAsync("u1", p.Id, showInMainCurrency: true);
        Assert.True(Assert.Single(await _portfolios.CardsAsync("u1")).ShowMain);

        // In the overview's main total it's a band of its own, at cost while there are no prices.
        var main = (await _overview.GetAsync("u1")).Main!;
        Assert.True(main.HasPortfolios);
        Assert.Equal(100000 + 93000, main.Portfolios);
        Assert.Equal(["Depot"], main.Bands.Select(b => b.Name));

        await _portfolios.SetOptionsAsync("u1", p.Id, inOthers: true);
        Assert.Equal(["Others"], (await _overview.GetAsync("u1")).Main!.Bands.Select(b => b.Name));
    }

    [Fact]
    public async Task AnAlertFiresOnceAndRearmsWhenTheFigureGoesBack()
    {
        var portfolio = await _portfolios.CreateAsync("u1", "Aktien");
        await _portfolios.AddHoldingAsync("u1", portfolio.Id, new HoldingInput("UBS", "CH0244767585", null, "CHF", new DateOnly(2026, 1, 5), 100, 25m));
        await SetPriceAsync(30m);
        await _alerts.AddAsync("u1", portfolio.Id, "CH0244767585", "CHF", new AlertInput(AlertKind.Price, true, 35m));
        await _alerts.AddAsync("u1", portfolio.Id, "CH0244767585", "CHF", new AlertInput(AlertKind.GainPercent, true, 10m));

        var list = await _alerts.ListAsync("u1", portfolio.Id);
        Assert.True(list[0].Armed);
        // Already above +10% (30 vs 25) when it was set: it waits for the gain to go back first.
        Assert.False(list[1].Armed);
        Assert.Equal(20m, list[1].Current);

        await SetPriceAsync(36m);
        Assert.Equal(1, await _alerts.CheckDueAsync());
        Assert.Equal(1, (await _overview.GetAsync("u1")).AlertsReached);

        // Still above: no second email. Nothing new to check without a new price update.
        Assert.Equal(0, await _alerts.CheckDueAsync());
        await SetPriceAsync(37m);
        Assert.Equal(0, await _alerts.CheckDueAsync());

        // Back below both limits, then above again: both fire.
        await SetPriceAsync(26m);
        Assert.Equal(0, await _alerts.CheckDueAsync());
        await SetPriceAsync(36m);
        Assert.Equal(2, await _alerts.CheckDueAsync());
    }

    private async Task SetPriceAsync(decimal price)
    {
        _clock.Now = _clock.Now.AddMinutes(1);
        await using var db = _factory.CreateDbContext();
        var row = await db.SecurityPrices.FirstOrDefaultAsync();
        if (row is null)
            db.SecurityPrices.Add(row = new SecurityPrice { UserId = "u1", Isin = "CH0244767585", Currency = "CHF" });
        row.Price = price;
        row.PriceDate = new DateOnly(2026, 10, 16);
        var s = await db.UserSettings.FirstAsync();
        s.PricesCheckedAt = _clock.Now.UtcDateTime;
        await db.SaveChangesAsync();
    }

    [Fact]
    public void AlertEmailListsTheReachedLimits()
    {
        var t = new Texts("de", CultureInfo.GetCultureInfo("de-CH"));
        var position = new SecurityPosition("UBS Group AG", "UBSG", "CH0244767585", "CHF", 300, 750000, 35.2m, new DateOnly(2026, 10, 16));
        var email = AlertEmail.Build(t, "Anna", [new FiredAlert("Aktien", 3, position, AlertKind.Price, true, 35m)], "https://lalira.example.com");

        Assert.Equal("LaLira-Alarm: UBS Group AG · Kurs über 35.00", email.Subject);
        Assert.Contains("Kurs 35.20 CHF (Schlusskurs vom 16.10.2026)", email.TextBody);
        Assert.Contains("Ihre 300 Titel: 10’560.00 CHF, +3’060.00 (+40.80%)", email.TextBody.Replace("'", "’"));
        Assert.Contains("https://lalira.example.com/portfolios/3", email.HtmlBody);
    }
}
