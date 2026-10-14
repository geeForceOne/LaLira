using System.Globalization;
using LaLira.Core.Ledger;

namespace LaLira.Tests;

public class MoneyTests
{
    [Theory]
    [InlineData("250", 25000)]
    [InlineData("1'234.50", 123450)]
    [InlineData("1’234.50", 123450)]
    [InlineData("1.234,50", 123450)]
    [InlineData("1,234.50", 123450)]
    [InlineData("12,5", 1250)]
    [InlineData("12.05", 1205)]
    [InlineData("-250", -25000)]
    [InlineData("250-", -25000)]
    [InlineData("−250", -25000)]
    [InlineData("(250.00)", -25000)]
    [InlineData("CHF 31'833.00", 3183300)]
    [InlineData("1 234,5", 123450)]
    [InlineData("1,234", 123400)]
    [InlineData("1.234.567", 123456700)]
    [InlineData("12.", 1200)]
    [InlineData("+80", 8000)]
    public void Parses(string input, long cents)
    {
        Assert.True(Money.TryParse(input, out var result), input);
        Assert.Equal(cents, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc,")]
    [InlineData("0.125")]
    [InlineData("12.3456")]
    [InlineData("1.234.5")]
    [InlineData("--5")]
    [InlineData("1,23,4")]
    [InlineData("5 % ")]
    public void Rejects(string input) => Assert.False(Money.TryParse(input, out _), input);

    [Fact]
    public void FormatsInSwissStyleWithRealMinus()
    {
        var ch = CultureInfo.GetCultureInfo("de-CH");
        var text = Money.Format(-3183350, ch);
        Assert.StartsWith("−", text);
        Assert.EndsWith("833.50", text);
        Assert.Equal("31" + ch.NumberFormat.NumberGroupSeparator + "834", Money.Format(3183350, ch, decimals: false));
    }

    [Fact]
    public void RoundingToZeroHasNoSign() => Assert.Equal("0", Money.Format(-40, CultureInfo.InvariantCulture, decimals: false));

    [Fact]
    public void InputFormatHasNoGroupSeparator() =>
        Assert.Equal("1234,50", Money.FormatForInput(123450, CultureInfo.GetCultureInfo("de-DE")));
}
