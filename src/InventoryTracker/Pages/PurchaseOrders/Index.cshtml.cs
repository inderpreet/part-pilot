using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.PurchaseOrders;

[Authorize(Policy = Policies.CanView)]
public class IndexModel(IPurchaseOrderService orders, ISupplierService suppliers, ICurrentUser currentUser, IClock clock) : PageModel
{
    public PurchaseOrderListQuery Query { get; private set; } = new();

    public PagedResult<PurchaseOrderListItem> Orders { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    /// <summary>For the supplier filter: every supplier, archived ones too (their old orders still exist).</summary>
    public IReadOnlyList<SupplierLookupItem> Suppliers { get; private set; } = [];

    /// <summary>The local calendar date, to highlight open orders that are past their expected date.</summary>
    public DateOnly Today { get; } = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());

    /// <summary>Only used to hide edit actions; the pages behind them enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    /// <summary>True when a search, supplier or date filter is applied (the tab alone is not a filter).</summary>
    public bool IsFiltered => Query.SearchTerm is not null || Query.SupplierId is not null || Query.From is not null || Query.To is not null;

    /// <summary>Full page normally; the tabs + table block (<c>#results</c>) for htmx filter / tab / sort / paging requests.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] PurchaseOrderListQuery query)
    {
        // With no tab chosen the list opens on what needs attention: orders that are waiting for goods.
        if (!Request.Query.ContainsKey("tab"))
        {
            query.Tab = PurchaseOrderTab.Open;
        }

        Query = query;
        Orders = await orders.SearchAsync(query, HttpContext.RequestAborted);
        Suppliers = await suppliers.LookupAsync(null, activeOnly: false, take: SupplierService.MaxLookupTake, cancellationToken: HttpContext.RequestAborted);

        return this.IsHtmx() ? Partial("_OrderResults", this) : Page();
    }
}