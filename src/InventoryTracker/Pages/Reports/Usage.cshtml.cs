using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Reports;

/// <summary>Units issued per part in a period, biggest consumers first (F06 FR-7). With no dates chosen it shows the last 30 days.</summary>
[Authorize(Policy = Policies.CanView)]
public class UsageModel(IReportService reports, ICategoryService categories, IClock clock) : PageModel
{
    public const int DefaultDays = 30;

    public UsageForm Query { get; private set; } = new();

    public UsageReport Report { get; private set; } = null!;

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    public bool IsFiltered => Query.SearchTerm is not null || Query.CategoryId is not null;

    public async Task<IActionResult> OnGetAsync([FromQuery] UsageForm query)
    {
        Query = WithDefaults(query);
        Report = await reports.GetUsageAsync(Query.ToFilter(), HttpContext.RequestAborted);

        if (this.IsHtmx())
        {
            return Partial("_UsageResults", this);
        }

        Categories = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        return Page();
    }

    public IActionResult OnGetCsv([FromQuery] UsageForm query)
    {
        var filter = WithDefaults(query).ToFilter();
        return new CsvResult(ReportPageExtensions.CsvFileName("usage", clock), (stream, ct) => reports.WriteUsageCsvAsync(filter, stream, ct));
    }

    /// <summary>Opening the report without any dates means "recently"; clearing both dates on purpose (the keys are sent, empty) means all time.</summary>
    private UsageForm WithDefaults(UsageForm query)
    {
        if (!Request.Query.ContainsKey("from") && !Request.Query.ContainsKey("to"))
        {
            query.From = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime()).AddDays(-DefaultDays);
        }

        return query;
    }

    public class UsageForm : ListQuery
    {
        public int? CategoryId { get; set; }

        public DateOnly? From { get; set; }

        public DateOnly? To { get; set; }

        /// <summary>The browser's time-zone offset in minutes; filled by site.js.</summary>
        public int? Tz { get; set; }

        public UsageFilter ToFilter()
        {
            var (from, to) = LocalTimeInput.DayRange(From, To, Tz);
            return new UsageFilter { Search = Search, Page = Page, PageSize = PageSize, Sort = Sort, Desc = Desc, CategoryId = CategoryId, FromUtc = from, ToUtcExclusive = to };
        }
    }
}
