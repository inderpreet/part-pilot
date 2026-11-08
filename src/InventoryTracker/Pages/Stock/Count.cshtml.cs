using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Stock;

/// <summary>
/// Physical stock count. Pick a slice of the shelves (category, bin, text), type what was counted, preview the differences,
/// then post: only products whose count differs from the system produce a <c>Count</c> transaction.
/// </summary>
/// <remarks>
/// The sheet is one form: the filters travel as hidden fields so Preview/Post/Back can rebuild it, and the typed counts
/// travel as <c>Counted[productId]</c> (a blank box means "not counted", which is different from a count of zero).
/// </remarks>
[Authorize(Policy = Policies.CanEdit)]
public class CountModel(IStockService stock, IProductService products, ICategoryService categories, ICurrentUser currentUser) : PageModel
{
    /// <summary>Most products one sheet shows; a bigger slice asks the user to narrow the filter.</summary>
    public const int MaxRows = 200;

    [BindProperty(SupportsGet = true)]
    public int? CategoryId { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Bin { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? Search { get; set; }

    /// <summary>Hide the system quantity so counters write down what they see, not what they expect.</summary>
    [BindProperty(SupportsGet = true)]
    public bool Blind { get; set; }

    [BindProperty]
    public Dictionary<int, int?> Counted { get; set; } = [];

    [BindProperty]
    public string? Note { get; set; }

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    public PagedResult<ProductListItem> Sheet { get; private set; } = new([], 1, MaxRows, 0);

    /// <summary>Set after "Preview": every counted product with its difference. <c>null</c> while editing.</summary>
    public IReadOnlyList<CountPreviewLine>? Preview { get; private set; }

    public bool HasFilter => CategoryId is not null || !string.IsNullOrWhiteSpace(Bin) || !string.IsNullOrWhiteSpace(Search);

    public IEnumerable<CountPreviewLine> Differences => Preview?.Where(l => l.Difference != 0) ?? [];

    public async Task<IActionResult> OnGetAsync()
    {
        await LoadSheetAsync();
        return Page();
    }

    /// <summary>Back to the sheet with whatever was typed so far (also what a filter change would do, but those are GETs).</summary>
    public async Task<IActionResult> OnPostEditAsync()
    {
        await LoadSheetAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPreviewAsync()
    {
        var entries = Entries();
        if (entries.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Enter at least one counted quantity. Leave a box empty for products you did not count.");
            await LoadSheetAsync();
            return Page();
        }

        Preview = await stock.PreviewCountAsync(entries, HttpContext.RequestAborted);
        await LoadCategoriesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostPostAsync()
    {
        var entries = Entries();
        if (entries.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Enter at least one counted quantity. Leave a box empty for products you did not count.");
            await LoadSheetAsync();
            return Page();
        }

        var known = (await stock.PreviewCountAsync(entries, HttpContext.RequestAborted)).ToDictionary(l => l.ProductId);
        var lines = entries
            .Select(e => new StockMovement(e.ProductId, StockTransactionType.Count, e.Counted, Reason: "Stock count", Note: Note))
            .ToList();

        var result = await stock.PostAsync(lines, currentUser.ToStockContext(), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ModelState.AddStockErrors(
                result,
                prefix: null,
                keepLineIndex: false,
                lineLabel: i => entries.Count > i && known.TryGetValue(entries[i].ProductId, out var line) ? $"{line.Sku}: " : $"Line {i + 1}: ");
            await LoadSheetAsync();
            return Page();
        }

        var changed = result.Value.Count(p => p.TransactionId is not null);
        var matched = result.Value.Count - changed;
        this.Flash(changed == 0
            ? $"Count recorded: all {matched} counted products already matched the system."
            : $"Count posted: {changed} {(changed == 1 ? "product" : "products")} adjusted, {matched} already matched.");
        return RedirectToPage("Movements", new { type = StockTransactionType.Count });
    }

    private List<CountEntry> Entries()
        => [.. Counted.Where(c => c.Value is not null).OrderBy(c => c.Key).Select(c => new CountEntry(c.Key, c.Value!.Value))];

    private async Task LoadSheetAsync()
    {
        await LoadCategoriesAsync();
        Sheet = await products.SearchAsync(
            new ProductListQuery
            {
                CategoryId = CategoryId,
                Bin = Bin,
                Search = Search,
                Status = ProductStatus.Active,
                Sort = "bin",
                Page = 1,
                PageSize = MaxRows,
            },
            HttpContext.RequestAborted);
    }

    private async Task LoadCategoriesAsync()
        => Categories = await categories.ListAsync(includeArchived: false, HttpContext.RequestAborted);
}
