using System.Globalization;
using InventoryTracker.Services;

namespace InventoryTracker.Tests.Services;

public class MoneyParserTests
{
    private static readonly CultureInfo US = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo DE = CultureInfo.GetCultureInfo("de-DE");

    [Theory]
    [InlineData("12.50", "en-US", 1250L)]
    [InlineData("12", "en-US", 1200L)]
    [InlineData("0", "en-US", 0L)]
    [InlineData("0.05", "en-US", 5L)]
    [InlineData(".5", "en-US", 50L)]
    [InlineData("  7.25  ", "en-US", 725L)]
    [InlineData("1.500", "en-US", 150L)] // trailing zeros are not extra precision
    [InlineData("-3.25", "en-US", -325L)]
    [InlineData("1,234.50", "en-US", 123450L)]
    [InlineData("1,234,567", "en-US", 123456700L)]
    [InlineData("12,50", "de-DE", 1250L)] // the current culture's decimal comma
    [InlineData("12.50", "de-DE", 1250L)] // falls back to invariant
    [InlineData("1.5", "de-DE", 150L)]
    [InlineData("12.50", "", 1250L)] // invariant culture
    public void Valid_amounts_become_cents(string text, string culture, long expected)
    {
        Assert.True(MoneyParser.TryParseCents(text, out var cents, CultureInfo.GetCultureInfo(culture)));
        Assert.Equal(expected, cents);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("12.3.4")]
    [InlineData("1.005")] // more than two decimals: refuse rather than silently round
    [InlineData("0.001")]
    [InlineData("1,5")] // must never be read as 15 in a dot-decimal culture
    [InlineData("1,23")]
    [InlineData("12,34,567")]
    [InlineData("1e3")]
    [InlineData("$5.00")]
    [InlineData("5.00 USD")]
    [InlineData("99999999999999999999")]
    [InlineData("92233720368547759")] // x100 overflows a long
    public void Invalid_amounts_are_rejected(string text)
    {
        Assert.False(MoneyParser.TryParseCents(text, out var cents, US));
        Assert.Equal(0L, cents);
    }

    [Fact]
    public void Null_is_rejected()
    {
        Assert.False(MoneyParser.TryParseCents(null, out _, US));
    }

    [Fact]
    public void A_comma_is_never_a_thousands_separator_in_a_comma_decimal_culture()
    {
        Assert.False(MoneyParser.TryParseCents("1.234,5x", out _, DE));
        Assert.True(MoneyParser.TryParseCents("1,5", out var cents, DE));
        Assert.Equal(150L, cents);
    }

    [Theory]
    [InlineData(0L, "0.00")]
    [InlineData(5L, "0.05")]
    [InlineData(1250L, "12.50")]
    [InlineData(123456789L, "1234567.89")]
    [InlineData(-325L, "-3.25")]
    [InlineData(long.MaxValue, "92233720368547758.07")]
    [InlineData(long.MinValue, "-92233720368547758.08")]
    public void Plain_strings_round_trip_through_the_parser(long cents, string expected)
    {
        var text = MoneyParser.ToPlainString(cents);
        Assert.Equal(expected, text);

        // Within parsing range (x100 must fit), the text parses back to the same amount.
        if (Math.Abs((decimal)cents) < long.MaxValue / 100m)
        {
            Assert.True(MoneyParser.TryParseCents(text, out var parsed, CultureInfo.InvariantCulture));
            Assert.Equal(cents, parsed);
        }
    }
}
