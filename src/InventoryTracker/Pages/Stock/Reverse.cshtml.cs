using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Stock;

/// <summary>
/// <c>POST /Stock/Reverse/{id}</c>: cancels a transaction with an opposite one. Posted by the "Reverse" buttons in the
/// history and movements tables, as a plain form (then redirects back) or with htmx (then reloads the page the button is on).
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class ReverseModel(IStockService stock, ICurrentUser currentUser) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("Movements");

    public async Task<IActionResult> OnPostAsync(long id, string? returnUrl)
    {
        var original = await stock.GetAsync(id, HttpContext.RequestAborted);
        if (original is null)
        {
            return NotFound();
        }

        var result = await stock.ReverseAsync(id, currentUser.ToStockContext(), cancellationToken: HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            var posted = result.Value;
            TempData[PageExtensions.FlashKey] =
                $"Reversed #{id} ({original.Sku}). On hand is now {posted.QuantityAfter}.";
        }
        else
        {
            TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        }

        // The message rides TempData, so show it by reloading the page the button was on (htmx) or redirecting back (plain form).
        if (this.IsHtmx())
        {
            Response.Headers["HX-Refresh"] = "true";
            return new NoContentResult();
        }

        return LocalRedirect(this.SafeReturnUrl(returnUrl, Url.Page("Movements")!));
    }
}
