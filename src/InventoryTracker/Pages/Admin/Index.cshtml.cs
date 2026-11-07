using InventoryTracker.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Admin;

/// <summary>Admin landing page. Later features add their admin tools (e.g. the audit log, F08) here.</summary>
[Authorize(Policy = Policies.RequireAdmin)]
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
