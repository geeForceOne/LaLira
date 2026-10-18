using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using LaLira.Core.Ledger;

namespace LaLira.Core.Prices;

// One listing of a security with its last close.
public sealed record PriceQuote(string Isin, string Currency, decimal Price, DateOnly PriceDate, string? Exchange, string? Ticker, bool IsPrimary);

// A search result: one listing of a security, for filling in a purchase. The last close comes
// along when the service has one.
public sealed record SecurityMatch(
    string Name, string Isin, string Currency, string? Exchange, string? Ticker, string? Type, bool IsPrimary, decimal? Price, DateOnly? PriceDate)
{
    public PriceQuote? Quote => Price is { } p && PriceDate is { } d ? new PriceQuote(Isin, Currency, p, d, Exchange, Ticker, IsPrimary) : null;
}

// A security's last close in a month.
public sealed record MonthClose(DateOnly Month, DateOnly Date, decimal Price);

public enum PriceProblem { BadKey, LimitReached, Unavailable }

public sealed class PriceSourceException(PriceProblem problem) : Exception(problem.ToString())
{
    public PriceProblem Problem { get; } = problem;
}

// Where prices come from. One call per ISIN returns all its listings; one call per search text
// returns the matching securities.
public interface IPriceSource
{
    Task<IReadOnlyList<PriceQuote>> LookupAsync(string apiKey, string isin, CancellationToken ct = default);

    Task<IReadOnlyList<SecurityMatch>> SearchAsync(string apiKey, string query, CancellationToken ct = default);

    // The month-end closes of one listing ("UBSG", "SW") from a date on, oldest first. One call.
    Task<IReadOnlyList<MonthClose>> MonthlyAsync(string apiKey, string ticker, string exchange, DateOnly from, CancellationToken ct = default);
}

// EODHD's search API (https://eodhd.com/financial-apis/search-api-for-stocks-etfs-mutual-funds):
// GET /api/search/{ticker, name or ISIN}?api_token=…&fmt=json lists the listings with name,
// currency, exchange, ISIN, the previous close and its date. Included in the free plan (20 calls
// a day).
// The history comes from the end-of-day API
// (https://eodhd.com/financial-apis/api-for-historical-data-and-volumes): GET /api/eod/{TICKER}.{EXCHANGE}
// ?period=m&from=YYYY-MM-DD&fmt=json returns one row per month (date, close, ...), one call for any
// length; the free plan reaches back one year.
public sealed class EodhdPriceSource(HttpClient http) : IPriceSource
{
    private const string BaseUrl = "https://eodhd.com/api/";
    public const int MaxMatches = 20;

    public async Task<IReadOnlyList<PriceQuote>> LookupAsync(string apiKey, string isin, CancellationToken ct = default) =>
        Parse(await SearchListingsAsync(apiKey, isin, 50, ct), isin);

    public async Task<IReadOnlyList<SecurityMatch>> SearchAsync(string apiKey, string query, CancellationToken ct = default) =>
        ParseMatches(await SearchListingsAsync(apiKey, query, MaxMatches, ct));

    public async Task<IReadOnlyList<MonthClose>> MonthlyAsync(string apiKey, string ticker, string exchange, DateOnly from, CancellationToken ct = default)
    {
        var symbol = Uri.EscapeDataString($"{ticker}.{exchange}");
        var url = $"{BaseUrl}eod/{symbol}?api_token={Uri.EscapeDataString(apiKey)}&fmt=json&period=m&from={from:yyyy-MM-dd}";
        return ParseMonths(await FetchAsync<List<Bar>>(url, ct));
    }

    private Task<List<Listing>> SearchListingsAsync(string apiKey, string query, int limit, CancellationToken ct) =>
        FetchAsync<List<Listing>>($"{BaseUrl}search/{Uri.EscapeDataString(query)}?api_token={Uri.EscapeDataString(apiKey)}&fmt=json&limit={limit}", ct);

    private async Task<T> FetchAsync<T>(string url, CancellationToken ct) where T : new()
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (HttpRequestException)
        {
            throw new PriceSourceException(PriceProblem.Unavailable);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PriceSourceException(PriceProblem.Unavailable);
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new PriceSourceException(PriceProblem.BadKey);
                case HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests:
                    throw new PriceSourceException(PriceProblem.LimitReached);
            }
            if (!response.IsSuccessStatusCode)
                throw new PriceSourceException(PriceProblem.Unavailable);

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(stream, Options, ct) ?? new T();
            }
            catch (JsonException)
            {
                throw new PriceSourceException(PriceProblem.Unavailable);
            }
        }
    }

    internal static IReadOnlyList<PriceQuote> Parse(IEnumerable<Listing> listings, string isin) =>
        ParseMatches(listings)
            .Where(m => string.Equals(m.Isin, isin, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Quote).OfType<PriceQuote>()
            .ToList();

    // Listings a purchase can be made from: a valid ISIN and a currency. Kept in the service's order
    // (most traded first).
    internal static IReadOnlyList<SecurityMatch> ParseMatches(IEnumerable<Listing> listings) =>
        listings
            .Where(l => l.Isin is { Length: 12 } isin && Isin.IsValid(isin.ToUpperInvariant()) && l.Currency is { Length: 3 })
            .Select(l =>
            {
                DateOnly? date = DateOnly.TryParseExact(l.PreviousCloseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
                decimal? price = l.PreviousClose is > 0 && date is not null ? l.PreviousClose : null;
                return new SecurityMatch((l.Name ?? l.Code ?? "").Trim(), l.Isin!.ToUpperInvariant(), l.Currency!.ToUpperInvariant(),
                    l.Exchange, l.Code, l.Type, l.IsPrimary == true, price, price is null ? null : date);
            })
            .ToList();

    // One close per month. A monthly row can be dated at the start of its month while its close is
    // the month's last, so the date kept is the month's end (the service caps it at today).
    internal static IReadOnlyList<MonthClose> ParseMonths(IEnumerable<Bar> bars) =>
        bars.Select(b => DateOnly.TryParseExact(b.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && b.Close is > 0
                ? new MonthClose(new DateOnly(d.Year, d.Month, 1), new DateOnly(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month)), b.Close.Value) : null)
            .OfType<MonthClose>()
            .GroupBy(m => m.Month).Select(g => g.Last())
            .OrderBy(m => m.Month).ToList();

    private static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    internal sealed class Listing
    {
        [JsonPropertyName("Code")] public string? Code { get; set; }
        [JsonPropertyName("Exchange")] public string? Exchange { get; set; }
        [JsonPropertyName("Name")] public string? Name { get; set; }
        [JsonPropertyName("Type")] public string? Type { get; set; }
        [JsonPropertyName("Currency")] public string? Currency { get; set; }
        [JsonPropertyName("ISIN")] public string? Isin { get; set; }
        [JsonPropertyName("isPrimary")] public bool? IsPrimary { get; set; }
        [JsonPropertyName("previousClose")] public decimal? PreviousClose { get; set; }
        [JsonPropertyName("previousCloseDate")] public string? PreviousCloseDate { get; set; }
    }

    internal sealed class Bar
    {
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("close")] public decimal? Close { get; set; }
    }
}
