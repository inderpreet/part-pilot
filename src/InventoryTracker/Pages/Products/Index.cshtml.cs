using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Products;

[Authorize(Policy = Policies.CanView)]
public class IndexModel(IProductService products, ICategoryService categories, ICurrentUser currentUser) : PageModel
{
    public ProductListQuery Query { get; private set; } = new();

    public PagedResult<ProductListItem> Products { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    /// <summary>Only used to hide edit actions; the pages behind them enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    /// <summary>True when any filter other than the default "active" status is applied.</summary>
    public bool IsFiltered => Query.SearchTerm is not null || Query.CategoryId is not null || Query.Status != ProductStatus.Active;

    /// <summary>The code typed/scanned when "Enter" found no exact SKU or barcode match.</summary>
    public string? UnmatchedCode { get; private set; }

    /// <summary>
    /// Full page normally; just the results block (<c>#results</c>) for htmx search / filter / sort / paging requests.
    /// Pressing Enter in the search box submits the form with <c>go=true</c> (model binding reads only true/false for a bool): an exact SKU or barcode match (a scanner
    /// types the code and presses Enter) opens that product straight away.
    /// </summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] ProductListQuery query, [FromQuery] bool go = false)
    {
        Query = query;

        if (go && !this.IsHtmx() && query.SearchTerm is { } code)
        {
            var match = await products.FindByCodeAsync(code, HttpContext.RequestAborted);
            if (match is not null)
            {
                return RedirectToPage("Detail", new { id = match.Id });
            }

            UnmatchedCode = code;
        }

        Products = await products.SearchAsync(query, HttpContext.RequestAborted);

        if (this.IsHtmx())
        {
            return Partial("_ProductRows", this);
        }

        Categories = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        return Page();
    }
}
