using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Products;

[Authorize(Policy = Policies.CanView)]
public class DetailModel(IProductService products, ICurrentUser currentUser) : PageModel
{
    public ProductDetail Product { get; private set; } = null!;

    /// <summary>Only used to hide edit actions; the handler and the edit page enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    /// <summary>Stock value at cost: on hand x unit cost.</summary>
    public long StockValueCents => Product.QuantityOnHand * Product.UnitCostCents;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var product = await products.GetAsync(id, HttpContext.RequestAborted);
        if (product is null)
        {
            return NotFound();
        }

        Product = product;
        return Page();
    }

    /// <summary>
    /// Archives (<c>active=false</c>) or restores (<c>active=true</c>). Works as a plain form post and, with htmx, as
    /// <c>hx-post</c> + <c>hx-confirm</c>; either way it ends on the refreshed detail page (a flash cannot ride an
    /// <c>HX-Redirect</c>, so the message goes through TempData).
    /// </summary>
    public async Task<IActionResult> OnPostArchiveAsync(int id, bool active, long rowVersion)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var existing = await products.GetAsync(id, HttpContext.RequestAborted);
        if (existing is null)
        {
            return NotFound();
        }

        var result = await products.SetActiveAsync(id, active, rowVersion, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            TempData[PageExtensions.FlashKey] = active ? $"Product '{existing.Sku}' restored." : $"Product '{existing.Sku}' archived.";
        }
        else
        {
            TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        }

        var url = Url.Page("Detail", new { id })!;
        return this.IsHtmx() ? this.HxRedirect(url) : Redirect(url);
    }
}
