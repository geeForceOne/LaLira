using LaLira.Core.Ledger;
using LaLira.Core.Localization;
using LaLira.Core.Services;

namespace LaLira.Tests;

public class IbanAndChartTests
{
    [Theory]
    [InlineData("CH93 0076 2011 6238 5295 7")]
    [InlineData("ch9300762011623852957")]
    [InlineData("DE89 3704 0044 0532 0130 00")]
    [InlineData("GB82 WEST 1234 5698 7654 32")]
    [InlineData("AT61 1904 3002 3457 3201")]
    public void ValidIbans(string text) => Assert.True(Iban.IsValid(Iban.Normalize(text)), text);

    [Theory]
    [InlineData("CH93 0076 2011 6238 5295 8")]   // wrong check
    [InlineData("CH93 0076 2011 6238 5295")]     // too short for CH
    [InlineData("XX00")]
    [InlineData("1234567890123456")]
    public void InvalidIbans(string text) => Assert.False(Iban.IsValid(Iban.Normalize(text)), text);

    [Fact]
    public void IbanIsShownInGroupsOfFour() =>
        Assert.Equal("CH93 0076 2011 6238 5295 7", Iban.Format("CH9300762011623852957"));

    [Fact]
    public void MonthEndBalancesUpToToday()
    {
        var positions = new List<LedgerPosition>
        {
            new(1, new DateOnly(2026, 1, 15), 1000, "a", null),
            new(2, new DateOnly(2026, 3, 1), -500, "b", null),
            new(3, new DateOnly(2026, 3, 20), 200, "upcoming", null),
        };
        var ends = LedgerBuilder.MonthEnds(10000, new DateOnly(2025, 12, 31), positions, new DateOnly(2026, 3, 10));
        Assert.Equal(
            [(new DateOnly(2025, 12, 1), 10000L), (new DateOnly(2026, 1, 1), 11000L), (new DateOnly(2026, 2, 1), 11000L), (new DateOnly(2026, 3, 1), 10500L)],
            ends);
    }

    [Fact]
    public void CsvFindsTheNoteColumnAndKeepsLineBreaks()
    {
        var table = CsvService.Parse("Datum;Beschreibung;Betrag;Notiz\n01.02.2026;Schrank;-3300;\"Ikea, zwei Teile\nRest im März\"\n");
        var map = CsvService.Guess(table);
        Assert.Equal(3, map.NoteColumn);
        Assert.Equal("Ikea, zwei Teile\nRest im März", table.Rows[1][3]);
    }

    [Fact]
    public void ChartRangeLabelsAreTranslated() =>
        Assert.All(new[] { "1 year", "5 years", "All" }, k => Assert.True(L.GermanDictionary.ContainsKey(k), k));

    [Theory]
    [InlineData(0, 1_000_000, new long[] { 0, 500_000, 1_000_000 })]          // 0 to 10'000: steps of 5'000
    [InlineData(-120_000, 380_000, new long[] { 0, 200_000 })]                  // steps of 2'000 across zero
    [InlineData(1_000, 1_050, new long[] { 1_000 })]                            // never below one whole unit
    [InlineData(1_234_567, 9_876_543, new long[] { 2_500_000, 5_000_000, 7_500_000 })]
    public void GridLinesAtRoundAmounts(double low, double high, long[] expected) =>
        Assert.Equal(expected, ChartScale.Ticks(low, high));

    [Fact]
    public void GridLinesNeverMoreThanFour()
    {
        foreach (var high in new[] { 150.0, 999.0, 12_345.0, 7_777_777.0, 98_765_432_100.0 })
            Assert.InRange(ChartScale.Ticks(-high / 3, high).Count, 1, 4);
    }
}
