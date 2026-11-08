using InventoryTracker.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Reports;

/// <summary>The list of reports (F06 FR-7). Every signed-in user may read reports, cost and valuation figures included.</summary>
[Authorize(Policy = Policies.CanView)]
public class IndexModel : PageModel
{
    public IReadOnlyList<ReportLink> Reports { get; } =
    [
        new("Low stock", "/Reports/LowStock", "Parts at or below their reorder point, counting what is on order, with a suggested quantity and one-click draft orders."),
        new("Stock valuation", "/Reports/Valuation", "What is on the shelves and what it is worth at current unit cost, by part and by category."),
        new("Stock movements", "/Reports/Movements", "Every receipt, issue, adjustment and count in a period, with totals in and out."),
        new("Usage", "/Reports/Usage", "Units issued per part over a period, biggest consumers first."),
        new("Purchase orders", "/Reports/PurchaseOrders", "Orders by date, supplier and status, with what is still to arrive and spend per supplier."),
        new("Dead stock", "/Reports/DeadStock", "Parts with stock that have not moved for a while, and the money tied up in them."),
    ];

    public void OnGet()
    {
    }

    public sealed record ReportLink(string Title, string Url, string Description);
}
