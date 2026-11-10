using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Requisitions;

/// <summary>
/// The line-by-line decision (F12 FR-4). "Approve all" is a link that refills the form rather than a decision, so nothing is
/// decided until the form is saved. The rules live in <see cref="IRequisitionService.DecideAsync"/>.
/// </summary>
[Authorize(Policy = Policies.CanApprove)]
public class DecideModel(IRequisitionService requisitions, ICurrentUser currentUser) : PageModel
{
    public RequisitionDetail Requisition { get; private set; } = null!;

    [BindProperty]
    public long RowVersion { get; set; }

    [BindProperty]
    public List<LineInput> Lines { get; set; } = [];

    public bool IsOwn => Requisition.RequestedById == currentUser.Id;

    private RequisitionActor Actor => currentUser.ToRequisitionActor();

    public async Task<IActionResult> OnGetAsync(int id, bool approveAll = false)
    {
        if (await LoadAsync(id) is { } missing)
        {
            return missing;
        }

        if (!Requisition.CanDecide)
        {
            this.FlashError($"{Requisition.Number} is not waiting for a decision.");
            return RedirectToPage("Detail", new { id });
        }

        RowVersion = Requisition.RowVersion;
        Lines = [.. Requisition.Lines.Select(l => new LineInput
        {
            LineId = l.Id,
            Decision = approveAll ? RequisitionDecision.Approved : null,
            QuantityApproved = l.QuantityRequested,
        })];
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (await LoadAsync(id) is { } missing)
        {
            return missing;
        }

        if (Lines.Count != Requisition.Lines.Count)
        {
            this.FlashError("This requisition changed since you opened it. Decide it again.");
            return RedirectToPage("Decide", new { id });
        }

        // The quantity of a rejected line is ignored, so an unreadable value there is not worth stopping for.
        for (var i = 0; i < Lines.Count; i++)
        {
            if (Lines[i].Decision == RequisitionDecision.Rejected)
            {
                ModelState.Remove(RequisitionFields.DecisionRow(i, RequisitionFields.QuantityApproved));
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await requisitions.DecideAsync(
            id,
            RowVersion,
            [.. Lines.Select(l => new RequisitionLineDecisionInput(l.LineId, l.Decision, l.QuantityApproved, l.Reason))],
            Actor,
            HttpContext.RequestAborted);

        if (result.Succeeded)
        {
            this.Flash($"{result.Value.Number} {result.Value.Status.ToString().ToLowerInvariant()}.");
            return RedirectToPage("Detail", new { id });
        }

        // A row's mistake is shown on the row. Anything else (decided by someone else, stale page) cannot be fixed on this form.
        if (result.Errors.Any(e => e.Field is null))
        {
            this.FlashError(string.Join(" ", result.Errors.Where(e => e.Field is null).Select(e => e.Message)));
            return RedirectToPage("Detail", new { id });
        }

        ModelState.AddErrors(result);
        return Page();
    }

    private async Task<IActionResult?> LoadAsync(int id)
    {
        var requisition = await requisitions.GetAsync(id, Actor, HttpContext.RequestAborted);
        if (requisition is null)
        {
            return NotFound();
        }

        Requisition = requisition;
        return null;
    }

    public sealed class LineInput
    {
        public long LineId { get; set; }

        public RequisitionDecision? Decision { get; set; }

        public int? QuantityApproved { get; set; }

        public string? Reason { get; set; }
    }
}
