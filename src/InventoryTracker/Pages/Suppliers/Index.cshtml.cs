using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Suppliers;

[Authorize(Policy = Policies.CanView)]
public class IndexModel(ISupplierService suppliers, ICurrentUser currentUser) : PageModel
{
    public SupplierListQuery Query { get; private set; } = new();

    public PagedResult<SupplierListItem> Suppliers { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    /// <summary>Only used to hide edit actions; the pages behind them enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    /// <summary>True when any filter other than the default "active" status is applied.</summary>
    public bool IsFiltered => Query.SearchTerm is not null || Query.Status != SupplierStatus.Active;

    /// <summary>Full page normally; just the results block (<c>#results</c>) for htmx search / filter / sort / paging requests.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] SupplierListQuery query)
    {
        Query = query;
        Suppliers = await suppliers.SearchAsync(query, HttpContext.RequestAborted);

        return this.IsHtmx() ? Partial("_SupplierRows", this) : Page();
    }
}
