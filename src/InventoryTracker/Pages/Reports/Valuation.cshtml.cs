using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Reports;

/// <summary>Stock on hand and its value at current unit cost, by part and by category (F06 FR-7).</summary>
[Authorize(Policy = Policies.CanView)]
public class ValuationModel(IReportService reports, ICategoryService categories, IClock clock) : PageModel
{
    public ValuationFilter Query { get; private set; } = new();

    public ValuationReport Report { get; private set; } = null!;

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    public bool IsFiltered => Query.SearchTerm is not null || Query.CategoryId is not null || Query.IncludeArchived;

    public async Task<IActionResult> OnGetAsync([FromQuery] ValuationFilter query)
    {
        Query = query;
        Report = await reports.GetValuationAsync(query, HttpContext.RequestAborted);

        if (this.IsHtmx())
        {
            return Partial("_ValuationResults", this);
        }

        Categories = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        return Page();
    }

    public IActionResult OnGetCsv([FromQuery] ValuationFilter query)
        => new CsvResult(ReportPageExtensions.CsvFileName("stock-valuation", clock), (stream, ct) => reports.WriteValuationCsvAsync(query, stream, ct));
}
