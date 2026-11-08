using InventoryTracker.Data.Entities;
using InventoryTracker.Services;

namespace InventoryTracker.Pages.Shared;

/// <summary>How purchase order statuses and dates are shown, so the list, detail, product and supplier pages agree.</summary>
public static class PurchaseOrderDisplay
{
    public static string StatusLabel(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Draft => "Draft",
        PurchaseOrderStatus.Submitted => "Submitted",
        PurchaseOrderStatus.PartiallyReceived => "Partially received",
        PurchaseOrderStatus.Received => "Received",
        PurchaseOrderStatus.Cancelled => "Cancelled",
        _ => status.ToString(),
    };

    /// <summary>The <c>badge-*</c> CSS class of a status (site.css).</summary>
    public static string StatusCss(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.Draft => "badge-draft",
        PurchaseOrderStatus.Submitted => "badge-submitted",
        PurchaseOrderStatus.PartiallyReceived => "badge-partial",
        PurchaseOrderStatus.Received => "badge-received",
        PurchaseOrderStatus.Cancelled => "badge-cancelled",
        _ => "badge-draft",
    };

    public static string TabLabel(PurchaseOrderTab tab) => tab switch
    {
        PurchaseOrderTab.All => "All",
        PurchaseOrderTab.Open => "Open",
        PurchaseOrderTab.Draft => "Drafts",
        PurchaseOrderTab.Received => "Received",
        PurchaseOrderTab.Cancelled => "Cancelled",
        _ => tab.ToString(),
    };

    /// <summary>An open order whose expected date has passed.</summary>
    public static bool IsOverdue(PurchaseOrderStatus status, DateOnly? expected, DateOnly today)
        => PurchaseOrderRules.IsOpen(status) && expected is { } date && date < today;

    /// <summary>A date for display; the order and expected dates are calendar dates, not instants, so no time-zone conversion.</summary>
    public static string Date(DateOnly? date) => date is { } value ? value.ToString("d", System.Globalization.CultureInfo.CurrentCulture) : "—";
}
