using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Reports;

/// <summary>Every stock movement in a period with totals in and out, newest first (F06 FR-7).</summary>
[Authorize(Policy = Policies.CanView)]
public class MovementsModel(IReportService reports, IStockService stock, ICategoryService categories, IClock clock) : PageModel
{
    public MovementsForm Query { get; private set; } = new();

    public MovementReport Report { get; private set; } = null!;

    public IReadOnlyList<CategoryListItem> Categories { get; private set; } = [];

    public IReadOnlyList<MovementUser> Users { get; private set; } = [];

    public bool IsFiltered =>
        Query.SearchTerm is not null || Query.Type is not null || Query.CategoryId is not null || !string.IsNullOrEmpty(Query.UserId)
        || Query.From is not null || Query.To is not null;

    public async Task<IActionResult> OnGetAsync([FromQuery] MovementsForm query)
    {
        Query = query;
        Report = await reports.GetMovementsAsync(query.ToFilter(), HttpContext.RequestAborted);

        if (this.IsHtmx())
        {
            return Partial("_MovementsResults", this);
        }

        Categories = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        Users = await stock.ListMovementUsersAsync(HttpContext.RequestAborted);
        return Page();
    }

    public IActionResult OnGetCsv([FromQuery] MovementsForm query)
    {
        var filter = query.ToFilter();
        return new CsvResult(ReportPageExtensions.CsvFileName("stock-movements", clock), (stream, ct) => reports.WriteMovementsCsvAsync(filter, stream, ct));
    }

    /// <summary>The filters as typed: whole days in the browser's time zone, turned into UTC instants by <see cref="ToFilter"/>.</summary>
    public class MovementsForm : ListQuery
    {
        public StockTransactionType? Type { get; set; }

        public int? CategoryId { get; set; }

        public string? UserId { get; set; }

        public DateOnly? From { get; set; }

        public DateOnly? To { get; set; }

        /// <summary>The browser's time-zone offset in minutes; filled by site.js.</summary>
        public int? Tz { get; set; }

        public MovementReportFilter ToFilter()
        {
            var (from, to) = LocalTimeInput.DayRange(From, To, Tz);
            return new MovementReportFilter
            {
                Search = Search, Page = Page, PageSize = PageSize,
                FromUtc = from, ToUtcExclusive = to, Type = Type, CategoryId = CategoryId, UserId = UserId,
            };
        }
    }
}
