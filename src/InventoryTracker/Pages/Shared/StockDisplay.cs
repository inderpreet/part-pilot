using System.Globalization;
using InventoryTracker.Data.Entities;

namespace InventoryTracker.Pages.Shared;

/// <summary>How stock movements are shown, so the movements list, the dashboard and the reports agree.</summary>
public static class StockDisplay
{
    /// <summary>The <c>badge-*</c> CSS class of a movement type (site.css).</summary>
    public static string TypeBadge(StockTransactionType type) => type switch
    {
        StockTransactionType.Receive or StockTransactionType.Return => "badge-ok",
        StockTransactionType.Issue => "badge-out",
        StockTransactionType.Adjust => "badge-low",
        StockTransactionType.Count => "badge-submitted",
        _ => "badge-draft",
    };

    /// <summary>A signed change such as <c>+5</c>, <c>-3</c> or <c>0</c>.</summary>
    public static string Delta(int delta) => delta.ToString("+#;-#;0", CultureInfo.InvariantCulture);
}
