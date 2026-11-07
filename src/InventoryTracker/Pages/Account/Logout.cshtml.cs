using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Account;

/// <summary>
/// Sign out is POST only (antiforgery-protected, so a malicious link or image cannot log someone out). Anonymous is
/// allowed on purpose: a user whose account was deactivated, or who has no role, must still be able to sign out
/// rather than hit "access denied" on the way out.
/// </summary>
[AllowAnonymous]
public class LogoutModel(ISignInService signIn) : PageModel
{
    public IActionResult OnGet() => LocalRedirect("/");

    public async Task<IActionResult> OnPostAsync()
    {
        await signIn.SignOutAsync(HttpContext.RequestAborted);
        this.Flash("You have been signed out.");
        return RedirectToPage("/Account/Login");
    }
}
