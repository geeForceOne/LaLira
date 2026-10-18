using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaLira.Core.Prices;

// The rate of one currency in another on a day: 1 From = Rate To.
public sealed record DayRate(DateOnly Date, decimal Rate);

public sealed class RateSourceException(bool unknownCurrency) : Exception(unknownCurrency ? "Unknown currency" : "Unavailable")
{
    // The service doesn't have this currency; asking again won't help.
    public bool UnknownCurrency { get; } = unknownCurrency;
}

// Where exchange rates come from.
public interface IRateSource
{
    // Daily rates from `from` to `to`, oldest first.
    Task<IReadOnlyList<DayRate>> DailyAsync(string fromCurrency, string toCurrency, DateOnly from, DateOnly to, CancellationToken ct = default);
}

// Frankfurter (https://frankfurter.dev): free, no key, no quota. GET /v2/rates?base=EUR&quotes=CHF
// &from=…&to=… returns one row per day ({"date","base","quote","rate"}), weekends included; an
// unknown currency answers 404 or 422. Its group=month gives an average, not the month's last
// rate, so the month ends are picked from the daily rows (checked 2026-10-17).
public sealed class FrankfurterRateSource(HttpClient http) : IRateSource
{
    private const string BaseUrl = "https://api.frankfurter.dev/v2/rates";

    public async Task<IReadOnlyList<DayRate>> DailyAsync(string fromCurrency, string toCurrency, DateOnly from, DateOnly to, CancellationToken ct = default)
    {
        var url = FormattableString.Invariant(
            $"{BaseUrl}?base={Uri.EscapeDataString(fromCurrency)}&quotes={Uri.EscapeDataString(toCurrency)}&from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}");
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, ct);
        }
        catch (HttpRequestException)
        {
            throw new RateSourceException(false);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new RateSourceException(false);
        }

        using (response)
        {
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity)
                throw new RateSourceException(true);
            if (!response.IsSuccessStatusCode)
                throw new RateSourceException(false);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            try
            {
                var rows = await JsonSerializer.DeserializeAsync<List<Row>>(stream, cancellationToken: ct) ?? [];
                return Parse(rows, toCurrency);
            }
            catch (JsonException)
            {
                throw new RateSourceException(false);
            }
        }
    }

    internal static IReadOnlyList<DayRate> Parse(IEnumerable<Row> rows, string toCurrency) =>
        rows.Where(r => string.Equals(r.Quote, toCurrency, StringComparison.OrdinalIgnoreCase) && r.Rate > 0)
            .Select(r => DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? new DayRate(d, r.Rate) : null)
            .OfType<DayRate>()
            .OrderBy(r => r.Date)
            .ToList();

    internal sealed class Row
    {
        [JsonPropertyName("date")] public string? Date { get; set; }
        [JsonPropertyName("quote")] public string? Quote { get; set; }
        [JsonPropertyName("rate")] public decimal Rate { get; set; }
    }
}
