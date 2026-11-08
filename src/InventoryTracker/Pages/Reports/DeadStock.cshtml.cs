using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Reports;

/// <summary>Parts with stock that have not moved for a number of days, and the money tied up in them (F06 FR-7).</summary>
[Authorize(Policy = Policies.CanView)]
public class DeadStockModel(IReportService reports, ICategoryService categories, IClock clock) : PageModel
{
    public DeadStockFilter Query { get; private set; } = new();

    public DeadStockReport Report { get; private set; } = null!;

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    public bool IsFiltered => Query.SearchTerm is not null || Query.CategoryId is not null;

    public async Task<IActionResult> OnGetAsync([FromQuery] DeadStockFilter query)
    {
        Query = query;
        Report = await reports.GetDeadStockAsync(query, HttpContext.RequestAborted);

        if (this.IsHtmx())
        {
            return Partial("_DeadStockResults", this);
        }

        Categories = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        return Page();
    }

    public IActionResult OnGetCsv([FromQuery] DeadStockFilter query)
        => new CsvResult(ReportPageExtensions.CsvFileName("dead-stock", clock), (stream, ct) => reports.WriteDeadStockCsvAsync(query, stream, ct));
}
