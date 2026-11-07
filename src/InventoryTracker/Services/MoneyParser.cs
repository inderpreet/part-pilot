using System.Globalization;
using System.Text.RegularExpressions;

namespace InventoryTracker.Services;

/// <summary>
/// Parses a typed money amount ("12.50", "12,50", "1,234.50") into <c>long</c> minor units (cents). Assumes two decimal
/// places, matching <c>MoneyFormatter</c> (single currency per install).
/// </summary>
public static partial class MoneyParser
{
    // Thousands separators are deliberately not accepted in the current culture: "1,5" must never be read as 15.
    private const NumberStyles Plain =
        NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite | NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    [GeneratedRegex(@"^\s*[+-]?\d{1,3}(,\d{3})+(\.\d{1,2})?\s*$")]
    private static partial Regex GroupedInvariant();

    /// <summary>
    /// Tries the given culture (default: the current one), then the invariant culture, then invariant with properly
    /// grouped thousands. Fails on empty text, more than two decimal places (never silently rounds) and values that
    /// do not fit in a <c>long</c> of cents. A negative amount parses; callers decide whether it is allowed.
    /// </summary>
    public static bool TryParseCents(string? text, out long cents, IFormatProvider? culture = null)
    {
        cents = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (decimal.TryParse(text, Plain, culture ?? CultureInfo.CurrentCulture, out var amount)
            || decimal.TryParse(text, Plain, CultureInfo.InvariantCulture, out amount)
            || (GroupedInvariant().IsMatch(text)
                && decimal.TryParse(text, Plain | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out amount)))
        {
            return ToCents(amount, out cents);
        }

        return false;
    }

    private static bool ToCents(decimal amount, out long cents)
    {
        cents = 0;
        if (amount != decimal.Round(amount, 2))
        {
            return false;
        }

        var scaled = amount * 100m;
        if (scaled > long.MaxValue || scaled < long.MinValue)
        {
            return false;
        }

        cents = (long)scaled;
        return true;
    }

    /// <summary>Cents as an editable plain amount ("12.50"), in the invariant culture so it round-trips through <see cref="TryParseCents"/>.</summary>
    public static string ToPlainString(long cents)
    {
        var negative = cents < 0;
        var absolute = negative ? (ulong)(-(cents + 1)) + 1UL : (ulong)cents;
        var text = $"{absolute / 100}.{absolute % 100:00}";
        return negative ? "-" + text : text;
    }
}
