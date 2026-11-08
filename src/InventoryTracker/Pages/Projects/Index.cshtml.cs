using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Projects;

[Authorize(Policy = Policies.CanView)]
public class IndexModel(IProjectService projects, ICurrentUser currentUser) : PageModel
{
    public ProjectListQuery Query { get; private set; } = new();

    public PagedResult<ProjectListItem> Projects { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    /// <summary>Only used to hide edit actions; the pages behind them enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    /// <summary>True when a search is applied (the tab alone is not a filter).</summary>
    public bool IsFiltered => Query.SearchTerm is not null;

    /// <summary>Full page normally; just the results block (<c>#results</c>) for htmx search / sort / paging requests.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] ProjectListQuery query)
    {
        Query = query;
        Projects = await projects.SearchAsync(query, HttpContext.RequestAborted);

        return this.IsHtmx() ? Partial("_ProjectResults", this) : Page();
    }
}
