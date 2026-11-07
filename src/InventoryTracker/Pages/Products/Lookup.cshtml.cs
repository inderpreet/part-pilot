using InventoryTracker.Auth;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Products;

/// <summary>
/// htmx endpoint behind the product picker (<c>_ProductPicker</c>): returns the dropdown rows for a query. Reused by
/// stock movements (F03) and purchase-order lines (F05). Read-only, so <c>CanView</c> is enough.
/// </summary>
[Authorize(Policy = Policies.CanView)]
public class LookupModel(IProductService products) : PageModel
{
    public async Task<IActionResult> OnGetAsync(string? q, bool activeOnly = true)
    {
        var items = await products.LookupAsync(q, activeOnly, cancellationToken: HttpContext.RequestAborted);
        return Partial("_ProductPickerResults", new ProductPickerResultsModel(items, q?.Trim()));
    }
}
