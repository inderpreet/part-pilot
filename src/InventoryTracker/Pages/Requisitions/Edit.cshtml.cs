using System.ComponentModel.DataAnnotations;
using System.Globalization;
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
/// What the lines block (<c>_RequisitionLines</c>) shows, plus what was typed into the add-line form when it failed.
/// <paramref name="UpdateRowVersion"/> is set on htmx responses: a line change moves the version, so the header form's copy is refreshed out of band.
/// </summary>
public sealed record RequisitionLinesModel(
    RequisitionDetail Requisition, int? ProductId = null, string? ProductLabel = null, string? Quantity = null, string? Note = null, bool UpdateRowVersion = false);

/// <summary>The dropdown of the add-line picker: active parts with availability.</summary>
public sealed record RequisitionProductResultsModel(IReadOnlyList<RequisitionProductChoice> Items, string? Query)
{
    public bool IsExact(RequisitionProductChoice item)
        => !string.IsNullOrEmpty(Query)
           && (string.Equals(item.Sku, Query, StringComparison.OrdinalIgnoreCase) || string.Equals(item.Barcode, Query, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Creates a draft (no id) or edits one. The needed-by date and reason are the header form; lines are edited in place through
/// htmx handlers that each answer with the whole lines block. Ownership and status rules are the service's.
/// </summary>
[Authorize(Policy = Policies.CanRequest)]
public class EditModel(IRequisitionService requisitions, ICurrentUser currentUser) : PageModel
{
    [BindProperty]
    public HeaderForm Input { get; set; } = new();

    public RequisitionDetail? Requisition { get; private set; }

    /// <summary>True when the save failed because someone else changed the requisition first; the page offers a reload link.</summary>
    public bool Conflict { get; private set; }

    private RequisitionActor Actor => currentUser.ToRequisitionActor();

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is null)
        {
            return Page();
        }

        Requisition = await requisitions.GetAsync(id.Value, Actor, HttpContext.RequestAborted);
        if (Requisition is null)
        {
            return NotFound();
        }

        if (!Requisition.CanEdit)
        {
            return ToDetail(Requisition);
        }

        Input = HeaderForm.From(Requisition);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        if (id is not null)
        {
            Requisition = await requisitions.GetAsync(id.Value, Actor, HttpContext.RequestAborted);
            if (Requisition is null)
            {
                return NotFound();
            }

            if (!Requisition.CanEdit)
            {
                return ToDetail(Requisition);
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var header = new RequisitionHeaderInput(Input.NeededBy, Input.Reason);
        var result = Requisition is null
            ? await requisitions.CreateDraftAsync(header, Actor, HttpContext.RequestAborted)
            : await requisitions.UpdateHeaderAsync(Requisition.Id, Input.RowVersion, header, Actor, HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            Conflict = result.Errors.Any(e => e.Code == "requisition.conflict");
            ModelState.AddErrors(result, nameof(Input));
            return Page();
        }

        TempData[PageExtensions.FlashKey] = Requisition is null ? $"Draft {result.Value.Number} created. Add the parts you need below." : $"{result.Value.Number} saved.";
        return RedirectToPage("Edit", new { id = result.Value.Id });
    }

    // ---------- Lines (htmx): each handler answers with the whole lines block ----------

    public async Task<IActionResult> OnGetProductsAsync(int id, string? q)
    {
        var requisition = await requisitions.GetAsync(id, Actor, HttpContext.RequestAborted);
        if (requisition is null)
        {
            return NotFound();
        }

        var items = await requisitions.LookupProductsAsync(id, q, Actor, HttpContext.RequestAborted);
        return Partial("_RequisitionProductResults", new RequisitionProductResultsModel(items, q?.Trim()));
    }

    public async Task<IActionResult> OnPostAddLineAsync(int id, int? productId, string? q, string? quantity, string? note)
    {
        if (!int.TryParse(quantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantityValue))
        {
            ModelState.AddModelError(RequisitionFields.Quantity, "Quantity must be a whole number, 1 or more.");
        }

        if (productId is null)
        {
            ModelState.AddModelError(RequisitionFields.ProductId, "Choose a part.");
        }

        if (ModelState.IsValid)
        {
            var result = await requisitions.AddLineAsync(id, new RequisitionLineInput(productId!.Value, quantityValue, note), Actor, HttpContext.RequestAborted);
            if (result.Succeeded)
            {
                this.Flash($"Added {result.Value.Sku}.");
                return await LinesAsync(id);
            }

            ModelState.AddErrors(result);
        }

        return await LinesAsync(id, productId, q, quantity, note);
    }

    public async Task<IActionResult> OnPostSaveLineAsync(int id, long lineId, string? quantity, string? note)
    {
        var key = LineKey(lineId);
        if (int.TryParse(quantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantityValue))
        {
            var result = await requisitions.UpdateLineAsync(id, lineId, quantityValue, note, Actor, HttpContext.RequestAborted);
            if (!result.Succeeded)
            {
                ModelState.AddErrors(result, key);
            }
        }
        else
        {
            ModelState.AddModelError($"{key}.{RequisitionFields.Quantity}", "Quantity must be a whole number, 1 or more.");
        }

        return await LinesAsync(id);
    }

    public async Task<IActionResult> OnPostRemoveLineAsync(int id, long lineId)
    {
        var result = await requisitions.RemoveLineAsync(id, lineId, Actor, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            this.Flash("Line removed.");
        }
        else
        {
            ModelState.AddErrors(result, LineKey(lineId));
        }

        return await LinesAsync(id);
    }

    public static string LineKey(long lineId) => $"line{lineId}";

    /// <summary>
    /// The lines block for htmx; failures answer 422 so the swap shows the messages. A requisition that can no longer be edited
    /// (submitted in another tab) goes to its detail page. Without htmx the messages ride TempData to the editor.
    /// </summary>
    private async Task<IActionResult> LinesAsync(int id, int? productId = null, string? productLabel = null, string? quantity = null, string? note = null)
    {
        var requisition = await requisitions.GetAsync(id, Actor, HttpContext.RequestAborted);
        if (requisition is null)
        {
            return NotFound();
        }

        if (!requisition.CanEdit)
        {
            TempData[PageExtensions.FlashErrorKey] = NotEditableMessage(requisition);
            var target = Url.Page("Detail", new { id })!;
            return this.IsHtmx() ? this.HxRedirect(target) : LocalRedirect(target);
        }

        if (!this.IsHtmx())
        {
            if (!ModelState.IsValid)
            {
                TempData[PageExtensions.FlashErrorKey] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            }

            return RedirectToPage("Edit", new { id });
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }

        return Partial("_RequisitionLines", new RequisitionLinesModel(requisition, productId, productLabel, quantity, note, UpdateRowVersion: true));
    }

    private RedirectToPageResult ToDetail(RequisitionDetail requisition)
    {
        TempData[PageExtensions.FlashErrorKey] = NotEditableMessage(requisition);
        return RedirectToPage("Detail", new { id = requisition.Id });
    }

    private static string NotEditableMessage(RequisitionDetail requisition)
        => requisition.CanChange
            ? $"{requisition.Number} is {RequisitionDisplay.StatusLabel(requisition.Status).ToLowerInvariant()} and can no longer be edited."
            : "Only the person who raised this requisition or an administrator can change it.";

    public sealed class HeaderForm
    {
        [Display(Name = "Needed by")]
        public DateOnly? NeededBy { get; set; }

        [StringLength(Data.Entities.Requisition.ReasonMaxLength, ErrorMessage = "Reason can be at most {1} characters.")]
        public string? Reason { get; set; }

        /// <summary>The version the form was loaded with; a stale one is reported as an edit conflict.</summary>
        public long RowVersion { get; set; }

        public static HeaderForm From(RequisitionDetail r) => new() { NeededBy = r.NeededBy, Reason = r.Reason, RowVersion = r.RowVersion };
    }
}
