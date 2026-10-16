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
}

// EODHD's search API (https://eodhd.com/financial-apis/search-api-for-stocks-etfs-mutual-funds):
// GET /api/search/{ticker, name or ISIN}?api_token=…&fmt=json lists the listings with name,
// currency, exchange, ISIN, the previous close and its date. Included in the free plan (20 calls
// a day).
public sealed class EodhdPriceSource(HttpClient http) : IPriceSource
{
    private const string BaseUrl = "https://eodhd.com/api/search/";
    public const int MaxMatches = 20;

    public async Task<IReadOnlyList<PriceQuote>> LookupAsync(string apiKey, string isin, CancellationToken ct = default) =>
        Parse(await FetchAsync(apiKey, isin, 50, ct), isin);

    public async Task<IReadOnlyList<SecurityMatch>> SearchAsync(string apiKey, string query, CancellationToken ct = default) =>
        ParseMatches(await FetchAsync(apiKey, query, MaxMatches, ct));

    private async Task<List<Listing>> FetchAsync(string apiKey, string query, int limit, CancellationToken ct)
    {
        var url = $"{BaseUrl}{Uri.EscapeDataString(query)}?api_token={Uri.EscapeDataString(apiKey)}&fmt=json&limit={limit}";
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
                return await JsonSerializer.DeserializeAsync<List<Listing>>(stream, Options, ct) ?? [];
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
}
