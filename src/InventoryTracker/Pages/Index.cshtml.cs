using InventoryTracker.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages;

[Authorize(Policy = Policies.CanView)]
public class IndexModel : PageModel
{
    public void OnGet()
    {
    }
}
