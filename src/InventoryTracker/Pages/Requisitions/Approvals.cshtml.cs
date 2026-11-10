using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Requisitions;

/// <summary>The approval queue: submitted requisitions, oldest first (F12 FR-3).</summary>
[Authorize(Policy = Policies.CanApprove)]
public class ApprovalsModel(IRequisitionService requisitions, ICurrentUser currentUser) : PageModel
{
    public PagedResult<ApprovalQueueItem> Queue { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    /// <summary>Full page normally; the table block (<c>#results</c>) for htmx paging.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] ListQuery query)
    {
        Queue = await requisitions.GetApprovalQueueAsync(query, currentUser.ToRequisitionActor(), HttpContext.RequestAborted);
        return this.IsHtmx() ? Partial("_ApprovalResults", this) : Page();
    }
}
