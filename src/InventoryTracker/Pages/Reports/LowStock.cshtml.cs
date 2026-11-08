using System.Globalization;
using System.Text.Json;
using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Reports;

/// <summary>
/// Parts at or below their reorder point, with a suggested quantity, and "Create draft POs" (F06 FR-4 to FR-6). Reading needs
/// <c>CanView</c>; the POST checks <c>CanEdit</c> itself. Creating orders redirects back (post/redirect/get) with the outcome in TempData.
/// </summary>
[Authorize(Policy = Policies.CanView)]
public class LowStockModel(IReorderService reorder, ICategoryService categories, ISupplierService suppliers, ICurrentUser currentUser) : PageModel
{
    private const string OutcomeKey = "ReorderOutcome";

    public ReorderFilter Query { get; private set; } = new();

    public PagedResult<ReorderSuggestion> Suggestions { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    public IReadOnlyList<SupplierLookupItem> Suppliers { get; private set; } = [];

    /// <summary>What the last "Create draft POs" did, shown once.</summary>
    public OutcomeView? Outcome { get; private set; }

    public bool CanEdit => currentUser.CanEdit;

    public bool IsFiltered => Query.SearchTerm is not null || Query.CategoryId is not null || Query.SupplierId is not null;

    /// <summary>Quantities as typed, by product id (<c>Quantities[12]=7</c>).</summary>
    [BindProperty]
    public Dictionary<int, string?> Quantities { get; set; } = [];

    /// <summary>The ticked rows (repeated <c>Selected=12</c>).</summary>
    [BindProperty]
    public List<int> Selected { get; set; } = [];

    /// <summary>Full page normally; just the table block (<c>#results</c>) for htmx filter, sort and paging requests.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] ReorderFilter query)
    {
        await LoadAsync(query);

        if (this.IsHtmx())
        {
            return Partial("_LowStockResults", this);
        }

        Categories = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        Suppliers = await suppliers.LookupAsync(null, activeOnly: false, take: SupplierService.MaxLookupTake, cancellationToken: HttpContext.RequestAborted);

        if (TempData[OutcomeKey] is string json)
        {
            Outcome = JsonSerializer.Deserialize<OutcomeView>(json);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync([FromQuery] ReorderFilter query)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var requests = Selected
            .Distinct()
            .Select(id => new ReorderRequest(
                id,
                Quantities.TryGetValue(id, out var text) && int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantity) ? quantity : 0))
            .ToList();

        if (requests.Count == 0)
        {
            this.FlashError("Tick at least one part to order.");
        }
        else if (requests.Count > ReorderService.MaxRequests)
        {
            this.FlashError($"Order at most {ReorderService.MaxRequests} parts at a time.");
        }
        else
        {
            var result = await reorder.CreateDraftPosAsync(requests, new PurchaseOrderActor(currentUser.Id!, currentUser.IsAdmin), HttpContext.RequestAborted);
            TempData[OutcomeKey] = JsonSerializer.Serialize(OutcomeView.From(result));
        }

        return RedirectToPage("/Reports/LowStock", new { query.Search, query.CategoryId, query.SupplierId, query.Sort, query.Desc, query.Page });
    }

    private async Task LoadAsync(ReorderFilter query)
    {
        Query = query;
        Suggestions = await reorder.GetSuggestionsAsync(query, HttpContext.RequestAborted);
    }

    /// <summary>
    /// The outcome in the little space a cookie allows (TempData is cookie-backed): the first few orders and skipped items,
    /// and how many more there were. Every created order is also a draft in the purchase order list.
    /// </summary>
    public sealed record OutcomeView(IReadOnlyList<CreatedView> Created, int MoreCreated, IReadOnlyList<SkippedView> Skipped, int MoreSkipped)
    {
        private const int MaxShown = 8;
        private const int MaxReasonLength = 120;

        public static OutcomeView From(ReorderDraftResult result) => new(
            [.. result.Created.Take(MaxShown).Select(c => new CreatedView(c.PurchaseOrderId, c.Number, c.SupplierId, c.SupplierName, c.LineCount, c.TotalCents))],
            Math.Max(0, result.Created.Count - MaxShown),
            [.. result.Skipped.Take(MaxShown).Select(s => new SkippedView(s.Sku, s.Name, s.Reason.Length > MaxReasonLength ? s.Reason[..MaxReasonLength] + "…" : s.Reason))],
            Math.Max(0, result.Skipped.Count - MaxShown));
    }

    public sealed record CreatedView(int Id, string Number, int SupplierId, string Supplier, int Lines, long TotalCents);

    public sealed record SkippedView(string Sku, string Name, string Reason);
}