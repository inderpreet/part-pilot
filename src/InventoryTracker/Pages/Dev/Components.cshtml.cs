using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Dev;

/// <summary>Style guide for the shared partials. Only available in the Development (and Testing) environments.</summary>
[Authorize(Policy = Policies.CanView)]
public class ComponentsModel(IWebHostEnvironment environment) : PageModel
{
    public ListQuery Query { get; private set; } = new();

    public PagerModel Pager { get; private set; } = new(1, ListQuery.DefaultPageSize, 0);

    private bool Enabled => environment.IsDevelopment() || environment.IsEnvironment("Testing");

    public IActionResult OnGet([FromQuery] ListQuery query)
    {
        if (!Enabled)
        {
            return NotFound();
        }

        Query = query;
        Pager = new PagerModel(query.SafePage, query.SafePageSize, TotalCount: 240);
        return Page();
    }

    /// <summary>Returns a modal fragment for <c>hx-get</c> into <c>#modal</c>.</summary>
    public IActionResult OnGetModal()
        => Enabled ? Partial("_DemoModal") : NotFound();

    /// <summary>Antiforgery-protected hx-post target: succeeds only with a valid token header.</summary>
    public IActionResult OnPostEcho()
    {
        if (!Enabled)
        {
            return NotFound();
        }

        Response.HxTrigger("flash", "Saved");
        return Content("posted", "text/plain");
    }

    /// <summary>Posts from inside the demo modal: flashes, then asks the client to close the dialog.</summary>
    public IActionResult OnPostSaveModal()
    {
        if (!Enabled)
        {
            return NotFound();
        }

        Response.HxTrigger("flash", "Saved from the modal");
        Response.HxTrigger("closeModal");
        return new NoContentResult();
    }

    /// <summary>Always fails, to show service errors rendered through the validation summary (HTTP 422).</summary>
    public IActionResult OnPostInvalid()
    {
        if (!Enabled)
        {
            return NotFound();
        }

        ModelState.AddErrors(Result.Failure(
            new Error("sku.duplicate", "SKU 'ABC-1' already exists.", "Sku"),
            new Error("cost.negative", "Unit cost cannot be negative.", "UnitCost")));
        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Partial("_ValidationSummary", ModelState);
    }
}
