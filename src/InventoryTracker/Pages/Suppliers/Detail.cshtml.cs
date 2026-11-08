using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Suppliers;

[Authorize(Policy = Policies.CanView)]
public class DetailModel(ISupplierService suppliers, IProductService products, IPurchaseOrderService purchaseOrders, ICurrentUser currentUser) : PageModel
{
    public SupplierDetail Supplier { get; private set; } = null!;

    /// <summary>The products this supplier provides (add / edit / remove / preferred through <c>/Suppliers/Links</c>).</summary>
    public SupplierLinksModel Links { get; private set; } = null!;

    /// <summary>The supplier's most recent purchase orders (newest order date first).</summary>
    public IReadOnlyList<PurchaseOrderListItem> RecentOrders { get; private set; } = [];

    /// <summary>Only used to hide edit actions; the handlers and the edit page enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var supplier = await suppliers.GetAsync(id, HttpContext.RequestAborted);
        if (supplier is null)
        {
            return NotFound();
        }

        Supplier = supplier;
        Links = (await SupplierLinksBuilder.BuildAsync(suppliers, products, LinkView.Supplier, id, CanEdit, cancellationToken: HttpContext.RequestAborted))!;
        RecentOrders = await purchaseOrders.GetRecentForSupplierAsync(id, cancellationToken: HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>
    /// Archives (<c>active=false</c>) or restores (<c>active=true</c>). Works as a plain form post and, with htmx, as
    /// <c>hx-post</c> + <c>hx-confirm</c>; either way it ends on the refreshed detail page (the message goes through TempData
    /// because a flash cannot ride an <c>HX-Redirect</c>).
    /// </summary>
    public async Task<IActionResult> OnPostArchiveAsync(int id, bool active, long rowVersion)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var existing = await suppliers.GetAsync(id, HttpContext.RequestAborted);
        if (existing is null)
        {
            return NotFound();
        }

        var result = await suppliers.SetActiveAsync(id, active, rowVersion, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            TempData[PageExtensions.FlashKey] = active ? $"Supplier '{existing.Name}' restored." : $"Supplier '{existing.Name}' archived.";
        }
        else
        {
            TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        }

        var url = Url.Page("Detail", new { id })!;
        return this.IsHtmx() ? this.HxRedirect(url) : Redirect(url);
    }
}
