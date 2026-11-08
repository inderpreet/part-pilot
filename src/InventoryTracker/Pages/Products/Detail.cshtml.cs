using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Products;

[Authorize(Policy = Policies.CanView)]
public class DetailModel(IProductService products, IStockService stock, ISupplierService suppliers, IPurchaseOrderService purchaseOrders, IProjectService projects, ICurrentUser currentUser) : PageModel
{
    public ProductDetail Product { get; private set; } = null!;

    /// <summary>The History tab: this product's ledger, newest posting first.</summary>
    public PagedResult<StockMovementItem> History { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    /// <summary>The Suppliers section: who supplies this product, with terms (managed through <c>/Suppliers/Links</c>).</summary>
    public SupplierLinksModel Suppliers { get; private set; } = null!;

    /// <summary>The Open POs section: order lines for this product that are still expected, soonest first.</summary>
    public IReadOnlyList<OpenOrderLineItem> OpenOrders { get; private set; } = [];

    /// <summary>The Open POs section also says how much of this product sits on Draft orders (intentions, not yet on order), with those drafts.</summary>
    public InDraftQuantity? InDraft { get; private set; }

    /// <summary>The Used in projects card: the projects whose BOM lists this product. Hidden when there are none.</summary>
    public IReadOnlyList<ProductProjectUse> UsedInProjects { get; private set; } = [];

    /// <summary>Only used to hide edit actions; the handler and the edit page enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    /// <summary>Stock value at cost: on hand x unit cost.</summary>
    public long StockValueCents => Product.QuantityOnHand * Product.UnitCostCents;

    public async Task<IActionResult> OnGetAsync(int id, [FromQuery] ListQuery query)
    {
        var product = await products.GetAsync(id, HttpContext.RequestAborted);
        if (product is null)
        {
            return NotFound();
        }

        Product = product;
        History = await stock.GetHistoryAsync(id, query, HttpContext.RequestAborted);
        Suppliers = (await SupplierLinksBuilder.BuildAsync(suppliers, products, LinkView.Product, id, CanEdit, cancellationToken: HttpContext.RequestAborted))!;
        OpenOrders = await purchaseOrders.GetOpenLinesForProductAsync(id, HttpContext.RequestAborted);
        InDraft = (await purchaseOrders.GetInDraftByProductAsync([id], HttpContext.RequestAborted)).GetValueOrDefault(id);
        UsedInProjects = await projects.GetProjectsUsingProductAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>Just the history table, for paging it in place (htmx).</summary>
    public async Task<IActionResult> OnGetHistoryAsync(int id, [FromQuery] ListQuery query)
    {
        var product = await products.GetAsync(id, HttpContext.RequestAborted);
        if (product is null)
        {
            return NotFound();
        }

        Product = product;
        History = await stock.GetHistoryAsync(id, query, HttpContext.RequestAborted);
        return Partial("_StockHistory", this);
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
            TempData[PageExtensions.FlashKey] = active ? $"Part '{existing.Sku}' restored." : $"Part '{existing.Sku}' archived.";
        }
        else
        {
            TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        }

        var url = Url.Page("Detail", new { id })!;
        return this.IsHtmx() ? this.HxRedirect(url) : Redirect(url);
    }
}
