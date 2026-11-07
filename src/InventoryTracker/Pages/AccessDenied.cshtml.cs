using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages;

/// <summary>Where the cookie handler sends a signed-in user who lacks a permission. Anonymous so it always renders.</summary>
[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public void OnGet()
    {
        // 403 with a body: StatusCodePages only re-executes for empty responses, so this page is shown as-is.
        Response.StatusCode = StatusCodes.Status403Forbidden;
    }
}
