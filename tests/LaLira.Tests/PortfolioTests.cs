using System.Globalization;
using LaLira.Core;
using LaLira.Core.Data;
using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Models;
using LaLira.Core.Prices;
using LaLira.Core.Security;
using LaLira.Core.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace LaLira.Tests;

// Portfolios against a real (in-memory) SQLite database, with a price source that answers from a list.
public sealed class PortfolioTests : IDisposable
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

    private sealed class FakeSource : IPriceSource
    {
        public List<PriceQuote> Quotes { get; } = [];
        public PriceProblem? Fail { get; set; }
        public List<string> Calls { get; } = [];

        public Task<IReadOnlyList<PriceQuote>> LookupAsync(string apiKey, string isin, CancellationToken ct = default)
        {
            Calls.Add(isin);
            if (Fail is { } problem)
                throw new PriceSourceException(problem);
            return Task.FromResult<IReadOnlyList<PriceQuote>>(Quotes.Where(q => q.Isin == isin).ToList());
        }

        public List<SecurityMatch> Matches { get; } = [];

        public Task<IReadOnlyList<SecurityMatch>> SearchAsync(string apiKey, string query, CancellationToken ct = default)
        {
            Calls.Add("search:" + query);
            if (Fail is { } problem)
                throw new PriceSourceException(problem);
            return Task.FromResult<IReadOnlyList<SecurityMatch>>(Matches.Where(m => m.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList());
        }

        // Month-end closes per "TICKER.EXCHANGE".
        public Dictionary<string, List<MonthClose>> History { get; } = [];

        public Task<IReadOnlyList<MonthClose>> MonthlyAsync(string apiKey, string ticker, string exchange, DateOnly from, CancellationToken ct = default)
        {
            Calls.Add($"history:{ticker}.{exchange}:{from:yyyy-MM}");
            if (Fail is { } problem)
                throw new PriceSourceException(problem);
            return Task.FromResult<IReadOnlyList<MonthClose>>(History.GetValueOrDefault($"{ticker}.{exchange}", []).Where(m => m.Month >= from).ToList());
        }
    }

    // Nestlé and the iShares Core MSCI World (both real, valid ISINs).
    private const string Nestle = "CH0038863350";
    private const string World = "IE00B4L5Y983";

    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly Factory _factory;
    private readonly Clock _clock = new(new DateTimeOffset(2026, 10, 16, 10, 0, 0, TimeSpan.Zero));
    private readonly FakeSource _source = new();
    private readonly PortfolioService _portfolios;
    private readonly PriceService _prices;

    public PortfolioTests()
    {
        _connection.Open();
        _factory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        using (var db = _factory.CreateDbContext())
        {
            db.Database.EnsureCreated();
            db.Users.Add(new AppUser { Id = "u1", UserName = "a@b.ch", Email = "a@b.ch" });
            db.Users.Add(new AppUser { Id = "u2", UserName = "c@d.ch", Email = "c@d.ch" });
            db.UserSettings.Add(new UserSettings { UserId = "u1", Language = "de", RegionFormat = "de-CH", TimeZone = "Europe/Zurich" });
            db.SaveChanges();
        }
        var settings = new SettingsService(_factory);
        _portfolios = new PortfolioService(_factory, new UserClock(_clock, settings));
        _prices = new PriceService(_factory, new EphemeralDataProtectionProvider(), _source, _clock, NullLogger<PriceService>.Instance);
    }

    public void Dispose() => _connection.Dispose();

    private static HoldingInput Buy(string isin, decimal quantity, decimal price, string currency = "CHF") =>
        new("Test", isin, null, currency, new DateOnly(2025, 3, 1), quantity, price);

    [Theory]
    [InlineData("CH0038863350", true)]
    [InlineData("IE00B4L5Y983", true)]
    [InlineData("US0378331005", true)]
    [InlineData("US0378331006", false)]
    [InlineData("CH003886335", false)]
    public void IsinCheckDigit(string isin, bool valid) => Assert.Equal(valid, Isin.IsValid(isin));

    [Theory]
    [InlineData("12.5", "de-CH", 12.5)]
    [InlineData("1’234.5", "de-CH", 1234.5)]
    [InlineData("0,125", "de-CH", 0.125)]
    [InlineData("1.234", "de-DE", 1234)]
    [InlineData("1.234,56", "de-DE", 1234.56)]
    [InlineData("12,3456", "de-DE", 12.3456)]
    [InlineData("1,234", "en-US", 1234)]
    [InlineData("87.1234", "en-US", 87.1234)]
    public void DecimalsAreReadInTheRegionFormat(string text, string region, double expected)
    {
        Assert.True(Decimals.TryParse(text, CultureInfo.GetCultureInfo(region), out var value));
        Assert.Equal((decimal)expected, value);
    }

    [Theory]
    [InlineData("-5")]
    [InlineData("1.2345678")]
    [InlineData("abc")]
    public void BadDecimalsAreRejected(string text) =>
        Assert.False(Decimals.TryParse(text, CultureInfo.GetCultureInfo("de-CH"), out _));

    [Fact]
    public async Task PartialSaleSplitsTheRow()
    {
        var p = await _portfolios.CreateAsync("u1", "ETF und Aktien");
        var id = await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 10, 80.5m));

        await _portfolios.SellAsync("u1", id, new SaleInput(new DateOnly(2026, 5, 1), 4, 100m));

        var view = await _portfolios.GetAsync("u1", p.Id);
        var held = Assert.Single(view.Holdings, h => !h.IsSold);
        var sold = Assert.Single(view.Holdings, h => h.IsSold);
        Assert.Equal(6, held.Quantity);
        Assert.Equal(48300, held.BuyTotal);
        Assert.Equal(4, sold.Quantity);
        Assert.Equal(40000, sold.SellTotal);
        Assert.Equal(7800, sold.Gain); // 400.00 - 322.00
        Assert.Equal(7800, Assert.Single(view.Totals).Realized);

        await Assert.ThrowsAsync<InputException>(() => _portfolios.SellAsync("u1", held.Id, new SaleInput(new DateOnly(2026, 5, 1), 7, 1m)));
        await Assert.ThrowsAsync<InputException>(() => _portfolios.SellAsync("u1", held.Id, new SaleInput(new DateOnly(2024, 5, 1), 1, 1m)));
    }

    [Fact]
    public async Task OthersCantSeeOrChangeIt()
    {
        var p = await _portfolios.CreateAsync("u1", "Depot");
        var id = await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 1, 1));
        await Assert.ThrowsAsync<NotFoundException>(() => _portfolios.GetAsync("u2", p.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => _portfolios.SellAsync("u2", id, new SaleInput(new DateOnly(2026, 1, 1), 1, 1)));
        await Assert.ThrowsAsync<NotFoundException>(() => _portfolios.AddHoldingAsync("u2", p.Id, Buy(Nestle, 1, 1)));
    }

    [Fact]
    public async Task InvalidIsinIsRejected()
    {
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await Assert.ThrowsAsync<InputException>(() => _portfolios.AddHoldingAsync("u1", p.Id, Buy("CH0038863351", 1, 1)));
    }

    [Fact]
    public async Task PricesGiveValueAndGainPerCurrency()
    {
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, Buy("ch 0038 8633 50", 10, 80m));
        await _portfolios.AddHoldingAsync("u1", p.Id, Buy(World, 2, 90m, "USD"));
        await _prices.SetKeyAsync("u1", "demo-key");
        _source.Quotes.Add(new PriceQuote(Nestle, "CHF", 85m, new DateOnly(2026, 10, 15), "SW", "NESN", true));
        _source.Quotes.Add(new PriceQuote(Nestle, "EUR", 90m, new DateOnly(2026, 10, 15), "F", "NESR", false));
        _source.Quotes.Add(new PriceQuote(World, "EUR", 95m, new DateOnly(2026, 10, 15), "XETRA", "EUNL", true));

        Assert.Equal(1, await _prices.RefreshAsync("u1"));
        Assert.Equal([Nestle, World], _source.Calls.Where(c => !c.StartsWith("history:")).Order());

        var view = await _portfolios.GetAsync("u1", p.Id);
        var chf = view.Totals.Single(t => t.Currency == "CHF");
        Assert.Equal(85000, chf.Value);
        Assert.Equal(5000, chf.Unrealized);
        Assert.Equal(6.25m, chf.UnrealizedPercent);
        // No USD listing: the row stays without a value.
        var usd = view.Totals.Single(t => t.Currency == "USD");
        Assert.Equal(1, usd.WithoutPrice);
        Assert.Equal(0, usd.Value);
        Assert.Null((await _prices.GetStatusAsync("u1")).Error);
    }

    [Fact]
    public async Task HistoryIsFetchedOnceAndGivesTheValueOverTime()
    {
        var p = await _portfolios.CreateAsync("u1", "Depot");
        // 10 bought on 1 March 2025 at 80, 5 of them sold in June 2026 at 95.
        var id = await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 10, 80m));
        await _portfolios.SellAsync("u1", id, new SaleInput(new DateOnly(2026, 6, 15), 5, 95m));
        await _prices.SetKeyAsync("u1", "demo-key");
        _source.Quotes.Add(new PriceQuote(Nestle, "CHF", 100m, new DateOnly(2026, 10, 15), "SW", "NESN", true));
        _source.History["NESN.SW"] =
        [
            new(new DateOnly(2025, 3, 1), new DateOnly(2025, 3, 31), 82m),
            new(new DateOnly(2025, 12, 1), new DateOnly(2025, 12, 31), 90m),
            new(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 98m),
        ];

        await _prices.RefreshAsync("u1");
        Assert.Contains("history:NESN.SW:2025-03", _source.Calls);
        _source.Calls.Clear();
        await _prices.RefreshAsync("u1");
        Assert.DoesNotContain(_source.Calls, c => c.StartsWith("history:"));

        var h = Assert.Single((await _portfolios.GetAsync("u1", p.Id)).History!);
        Assert.Equal(new DateOnly(2025, 3, 1), h.Months[0].Month);
        Assert.Equal(82000, h.Months[0].Value);
        Assert.Equal(80000, h.Months[0].Cost);
        // A month without a close takes the latest one before it.
        Assert.Equal(82000, h.Months.Single(m => m.Month == new DateOnly(2025, 11, 1)).Value);
        // After the sale only the rest counts.
        Assert.Equal(45000, h.Months.Single(m => m.Month == new DateOnly(2026, 6, 1)).Value);
        // This month: the current price.
        Assert.Equal(50000, h.Months[^1].Value);
        Assert.Equal(40000, h.Months[^1].Cost);
        // 2025: worth 900 at the end for 800 bought.
        var y2025 = h.Years.Single(y => y.Year == 2025);
        Assert.Equal(10000, y2025.Gain);
        Assert.Equal(12.5m, y2025.Percent);
    }

    [Fact]
    public async Task YearsBeforeThePriceHistoryAreLeftOut()
    {
        // Bought in 2022, but the closes only reach back to November 2025.
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 10, 80m) with { BuyDate = new DateOnly(2022, 5, 1) });
        await _prices.SetKeyAsync("u1", "demo-key");
        _source.Quotes.Add(new PriceQuote(Nestle, "CHF", 100m, new DateOnly(2026, 10, 15), "SW", "NESN", true));
        _source.History["NESN.SW"] = [new(new DateOnly(2025, 11, 1), new DateOnly(2025, 11, 30), 90m)];

        await _prices.RefreshAsync("u1");
        var h = Assert.Single((await _portfolios.GetAsync("u1", p.Id)).History!);
        Assert.Equal(2022, h.Months[0].Month.Year);
        // 2025 is kept and carries the gain since the purchase.
        Assert.Equal([2025, 2026], h.Years.Select(y => y.Year));
        Assert.Equal(10000, h.Years[0].Gain);
    }

    [Fact]
    public async Task HistoryWithThisMonthKeepsTheCurrentClose()
    {
        // EODHD's monthly rows include the current month, which the refresh has just kept too.
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 10, 80m));
        await _prices.SetKeyAsync("u1", "demo-key");
        _source.Quotes.Add(new PriceQuote(Nestle, "CHF", 100m, new DateOnly(2026, 10, 15), "SW", "NESN", true));
        _source.History["NESN.SW"] =
        [
            new(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), 98m),
            new(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), 99m),
        ];

        Assert.Equal(1, await _prices.RefreshAsync("u1"));
        await using var db = _factory.CreateDbContext();
        var months = db.SecurityMonthPrices.Where(m => m.Isin == Nestle).OrderBy(m => m.Month).ToList();
        Assert.Equal([new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1)], months.Select(m => m.Month));
        Assert.Equal(100m, months[^1].Price);
        Assert.NotNull(db.SecurityPrices.Single(x => x.Isin == Nestle).HistoryFetchedAt);
    }

    [Fact]
    public async Task ARejectedKeyIsReported()
    {
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 1, 1));
        await _prices.SetKeyAsync("u1", "wrong");
        _source.Fail = PriceProblem.BadKey;

        await _prices.RefreshAsync("u1");

        var status = await _prices.GetStatusAsync("u1");
        Assert.Equal(SecretStatus.Set, status.Key);
        Assert.Equal(PriceService.BadKeyMessage, status.Error);
        Assert.NotNull(status.CheckedAt);
    }

    [Fact]
    public async Task DailyRefreshRunsOnceAfterSix()
    {
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 1, 1));
        await _prices.SetKeyAsync("u1", "key");

        _clock.Now = new DateTimeOffset(2026, 10, 16, 3, 30, 0, TimeSpan.Zero); // 05:30 in Zurich
        Assert.Equal(0, await _prices.RefreshDueAsync());
        _clock.Now = new DateTimeOffset(2026, 10, 16, 4, 1, 0, TimeSpan.Zero); // 06:01
        Assert.Equal(1, await _prices.RefreshDueAsync());
        _clock.Now = new DateTimeOffset(2026, 10, 16, 15, 0, 0, TimeSpan.Zero);
        Assert.Equal(0, await _prices.RefreshDueAsync());
        _clock.Now = new DateTimeOffset(2026, 10, 17, 4, 1, 0, TimeSpan.Zero);
        Assert.Equal(1, await _prices.RefreshDueAsync());
        Assert.Equal(2, _source.Calls.Count);
    }

    [Fact]
    public async Task CsvExportHasEveryRow()
    {
        var p = await _portfolios.CreateAsync("u1", "ETF und Aktien");
        var id = await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 10, 80.5m) with { Name = "Nestlé; Namen", Ticker = " NESN " });
        await _portfolios.SellAsync("u1", id, new SaleInput(new DateOnly(2026, 5, 1), 4, 100m));
        var settings = new SettingsService(_factory);
        var clock = new UserClock(_clock, settings);
        var accounts = new AccountService(_factory);
        var csv = new CsvService(_factory, accounts, new LedgerService(_factory, accounts, clock), _portfolios);

        var (name, content) = await csv.ExportPortfolioAsync("u1", p.Id, new Texts("de", CultureInfo.GetCultureInfo("de-CH")));

        var lines = System.Text.Encoding.UTF8.GetString(content).TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("ETF-und-Aktien-", name);
        Assert.Equal(3, lines.Length);
        Assert.StartsWith("Name;Ticker;ISIN;Valor;Währung;Kaufdatum;Anzahl", lines[0]);
        Assert.Equal("\"Nestlé; Namen\";NESN;CH0038863350;;CHF;01.03.2025;6;80.5;483.00;;;;;;;", lines[1]);
        Assert.Equal("\"Nestlé; Namen\";NESN;CH0038863350;;CHF;01.03.2025;4;80.5;322.00;01.05.2026;100;400.00;;;400.00;78.00", lines[2]);
    }

    [Fact]
    public async Task SearchIsCachedAndAPickedMatchNeedsNoSecondCall()
    {
        await Assert.ThrowsAsync<InputException>(() => _prices.SearchAsync("u1", "ubs")); // no key yet
        await _prices.SetKeyAsync("u1", "key");
        const string Ubs = "CH0244767585";
        _source.Matches.Add(new SecurityMatch("UBS Group AG", Ubs, "CHF", "SW", "UBSG", "Common Stock", true, 27.85m, new DateOnly(2026, 10, 15)));

        var found = Assert.Single(await _prices.SearchAsync("u1", "  ubs "));
        Assert.Single(await _prices.SearchAsync("u1", "UBS"));
        Assert.Equal(["search:ubs"], _source.Calls);

        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, new HoldingInput(found.Name, found.Isin, null, found.Currency, new DateOnly(2026, 1, 5), 100, 22m));
        Assert.Equal(1, await _prices.RefreshAsync("u1", Ubs));
        // The price came from the search; the one call is the security's past month ends.
        Assert.Equal(["search:ubs", "history:UBSG.SW:2026-01"], _source.Calls);

        var held = Assert.Single((await _portfolios.GetAsync("u1", p.Id)).Holdings);
        Assert.Equal(278500, held.Value);
    }

    [Fact]
    public void SearchKeepsOnlyListingsWithIsinAndCurrency()
    {
        var listings = new[]
        {
            new EodhdPriceSource.Listing { Code = "UBSG", Exchange = "SW", Name = "UBS Group AG", Currency = "CHF", Isin = "CH0244767585", IsPrimary = true, PreviousClose = 27.85m, PreviousCloseDate = "2026-10-15" },
            new EodhdPriceSource.Listing { Code = "UBS", Exchange = "US", Name = "UBS Group AG", Currency = "USD", Isin = "CH0244767585" },
            new EodhdPriceSource.Listing { Code = "X", Exchange = "INDX", Name = "Some index", Currency = "CHF", Isin = null },
        };
        var matches = EodhdPriceSource.ParseMatches(listings);
        Assert.Equal(2, matches.Count);
        Assert.Equal(27.85m, matches[0].Price);
        Assert.Null(matches[1].Price);
    }

    [Theory]
    [InlineData("CH0244767585", "24476758")]
    [InlineData("ch 0038 8633 50", "3886335")]
    [InlineData("CH0012005267", "1200526")]
    [InlineData("IE00B4L5Y983", null)]
    [InlineData("CH0244767586", null)]
    public void SwissIsinsContainTheValor(string isin, string? valor) => Assert.Equal(valor, Isin.Valor(isin));

    [Fact]
    public async Task OwnTickerWinsOverThePriceListing()
    {
        var p = await _portfolios.CreateAsync("u1", "Depot");
        await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 1, 1));
        var own = await _portfolios.AddHoldingAsync("u1", p.Id, Buy(Nestle, 2, 1) with { Ticker = "Nestlé N" });
        await _prices.SetKeyAsync("u1", "key");
        _source.Quotes.Add(new PriceQuote(Nestle, "CHF", 85m, new DateOnly(2026, 10, 15), "SW", "NESN", true));
        await _prices.RefreshAsync("u1");

        var rows = (await _portfolios.GetAsync("u1", p.Id)).Holdings;
        Assert.Equal("Nestlé N", rows.Single(r => r.Id == own).ShownTicker);
        Assert.Equal("NESN", rows.Single(r => r.Id != own).ShownTicker);

        // A sold row keeps showing the listing's ticker.
        await _portfolios.SellAsync("u1", rows.Single(r => r.Id != own).Id, new SaleInput(new DateOnly(2026, 10, 16), 1, 90m));
        Assert.Equal("NESN", (await _portfolios.GetAsync("u1", p.Id)).Holdings.Single(r => r.IsSold).ShownTicker);
    }

    [Fact]
    public void PickPrefersTheNewestCloseInTheCurrency()
    {
        var quotes = new[]
        {
            new PriceQuote(Nestle, "CHF", 80m, new DateOnly(2026, 10, 1), "OLD", null, true),
            new PriceQuote(Nestle, "CHF", 85m, new DateOnly(2026, 10, 15), "SW", null, false),
            new PriceQuote(Nestle, "CHF", 86m, new DateOnly(2026, 10, 15), "SW", null, true),
        };
        Assert.Equal(86m, PriceService.Pick(quotes, "CHF")!.Price);
        Assert.Null(PriceService.Pick(quotes, "USD"));
    }

    [Fact]
    public void EodhdListingsAreRead()
    {
        var listings = new[]
        {
            new EodhdPriceSource.Listing { Code = "NESN", Exchange = "SW", Currency = "CHF", Isin = Nestle, IsPrimary = true, PreviousClose = 85.12m, PreviousCloseDate = "2026-10-15" },
            new EodhdPriceSource.Listing { Code = "X", Exchange = "Y", Currency = "CHF", Isin = "US0378331005", PreviousClose = 1m, PreviousCloseDate = "2026-10-15" },
            new EodhdPriceSource.Listing { Code = "Z", Exchange = "Y", Currency = "EUR", Isin = Nestle, PreviousClose = null, PreviousCloseDate = "2026-10-15" },
        };
        var quote = Assert.Single(EodhdPriceSource.Parse(listings, Nestle));
        Assert.Equal(85.12m, quote.Price);
        Assert.Equal(new DateOnly(2026, 10, 15), quote.PriceDate);
        Assert.True(quote.IsPrimary);
    }
}
