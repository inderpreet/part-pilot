using System.Globalization;

namespace InventoryTracker.Hosting;

/// <summary>Formats money stored as <c>long</c> minor units (cents). Assumes two decimal places (architecture: single currency).</summary>
public static class MoneyFormatter
{
    public static string Format(long cents, string symbol)
    {
        // Work in decimal for display only; storage stays integral. long.MinValue is guarded by using unsigned math.
        var absolute = cents < 0 ? (ulong)(-(cents + 1)) + 1UL : (ulong)cents;
        var whole = absolute / 100;
        var fraction = absolute % 100;

        var text = $"{symbol}{whole.ToString("N0", CultureInfo.InvariantCulture)}.{fraction:00}";
        return cents < 0 ? "-" + text : text;
    }
}
