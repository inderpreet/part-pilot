using InventoryTracker.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Stock;

/// <summary>The "Stock" navigation entry: the global movements list is the landing page of the section.</summary>
[Authorize(Policy = Policies.CanView)]
public class IndexModel : PageModel
{
    public IActionResult OnGet() => RedirectToPage("Movements");
}
