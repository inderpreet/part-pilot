using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Stock;

/// <summary>The global stock ledger, newest posting first, with filters. Read-only, so <c>CanView</c> is enough (Reverse checks <c>CanEdit</c> itself).</summary>
[Authorize(Policy = Policies.CanView)]
public class MovementsModel(IStockService stock, IProductService products, ICurrentUser currentUser) : PageModel
{
    public MovementFilter Query { get; private set; } = new();

    public PagedResult<StockMovementItem> Movements { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    public IReadOnlyList<MovementUser> Users { get; private set; } = [];

    /// <summary>The product the list is narrowed to (from a "view all movements" link), if any.</summary>
    public ProductDetail? Product { get; private set; }

    public bool CanEdit => currentUser.CanEdit;

    public bool IsFiltered =>
        Query.SearchTerm is not null || Query.ProductId is not null || Query.Type is not null || !string.IsNullOrEmpty(Query.UserId)
        || Query.From is not null || Query.To is not null || Query.ImportBatchId is not null;

    /// <summary>Where a no-JavaScript Reverse button returns to: this page with its current filters.</summary>
    public string CurrentUrl => $"{Request.Path}{Request.QueryString}";

    /// <summary>Full page normally; just the results block (<c>#results</c>) for htmx filter and paging requests.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] MovementFilter query)
    {
        Query = query;
        var (from, to) = LocalTimeInput.DayRange(query.From, query.To, query.Tz);

        Movements = await stock.SearchMovementsAsync(
            new MovementQuery
            {
                Search = query.Search,
                Page = query.Page,
                PageSize = query.PageSize,
                ProductId = query.ProductId,
                Type = query.Type,
                UserId = query.UserId,
                ImportBatchId = query.ImportBatchId,
                From = from,
                To = to,
            },
            HttpContext.RequestAborted);

        if (this.IsHtmx())
        {
            return Partial("_MovementResults", this);
        }

        Users = await stock.ListMovementUsersAsync(HttpContext.RequestAborted);
        if (query.ProductId is { } productId)
        {
            Product = await products.GetAsync(productId, HttpContext.RequestAborted);
        }

        return Page();
    }

    /// <summary>Filters as typed: whole days (local to the browser, see <see cref="LocalTimeInput"/>) rather than instants.</summary>
    public class MovementFilter : ListQuery
    {
        public int? ProductId { get; set; }

        public StockTransactionType? Type { get; set; }

        public string? UserId { get; set; }

        /// <summary>Set by the link on an import summary (F07).</summary>
        public long? ImportBatchId { get; set; }

        public DateOnly? From { get; set; }

        public DateOnly? To { get; set; }

        /// <summary>The browser's time-zone offset in minutes; filled by site.js.</summary>
        public int? Tz { get; set; }
    }
}
