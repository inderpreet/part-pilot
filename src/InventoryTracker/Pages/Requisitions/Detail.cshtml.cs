using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Requisitions;

/// <summary>
/// One requisition: header, lines with availability, status history and the lifecycle buttons. A requisition the user may not
/// see is a 404, on every handler. Each action works as a plain form post and, with htmx, as <c>hx-post</c> + <c>hx-confirm</c>.
/// </summary>
[Authorize(Policy = Policies.CanRequest)]
public class DetailModel(IRequisitionService requisitions, ICurrentUser currentUser) : PageModel
{
    public RequisitionDetail Requisition { get; private set; } = null!;

    private RequisitionActor Actor => currentUser.ToRequisitionActor();

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var requisition = await requisitions.GetAsync(id, Actor, HttpContext.RequestAborted);
        if (requisition is null)
        {
            return NotFound();
        }

        Requisition = requisition;
        return Page();
    }

    public Task<IActionResult> OnPostSubmitAsync(int id, long rowVersion)
        => ActAsync(id, async () => await requisitions.SubmitAsync(id, rowVersion, Actor, HttpContext.RequestAborted), "{0} submitted.");

    public Task<IActionResult> OnPostWithdrawAsync(int id, long rowVersion)
        => ActAsync(id, async () => await requisitions.WithdrawAsync(id, rowVersion, Actor, HttpContext.RequestAborted), "{0} withdrawn; it is a draft again.");

    public Task<IActionResult> OnPostDeleteAsync(int id, long rowVersion)
        => ActAsync(id, async () => await requisitions.DeleteDraftAsync(id, rowVersion, Actor, HttpContext.RequestAborted), "Draft {0} deleted.", toListOnSuccess: true);

    /// <summary>Open to approvers only, which the page-level policy cannot express per handler.</summary>
    public async Task<IActionResult> OnPostCancelAsync(int id, long rowVersion, string? reason)
    {
        if (!await this.CanApproveAsync())
        {
            return Forbid();
        }

        return await ActAsync(id, async () => await requisitions.CancelSubmittedAsync(id, rowVersion, reason, Actor, HttpContext.RequestAborted), "{0} cancelled.");
    }

    /// <summary>F13 FR-10: cancel an approved requisition while none of it is on a purchase order. Approvers only.</summary>
    public async Task<IActionResult> OnPostCancelApprovedAsync(int id, long rowVersion, string? reason)
    {
        if (!await this.CanApproveAsync())
        {
            return Forbid();
        }

        return await ActAsync(id, async () => await requisitions.CancelApprovedAsync(id, rowVersion, reason, Actor, HttpContext.RequestAborted), "{0} cancelled.");
    }

    private async Task<IActionResult> ActAsync(int id, Func<Task<Result>> action, string success, bool toListOnSuccess = false)
    {
        var requisition = await requisitions.GetAsync(id, Actor, HttpContext.RequestAborted);
        if (requisition is null)
        {
            return NotFound();
        }

        var result = await action();
        if (result.Succeeded)
        {
            TempData[PageExtensions.FlashKey] = string.Format(System.Globalization.CultureInfo.InvariantCulture, success, requisition.Number);
        }
        else
        {
            TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        }

        var target = result.Succeeded && toListOnSuccess ? Url.Page("Index")! : Url.Page("Detail", new { id })!;
        return this.IsHtmx() ? this.HxRedirect(target) : LocalRedirect(target);
    }
}
