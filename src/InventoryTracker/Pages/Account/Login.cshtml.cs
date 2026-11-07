using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Account;

[AllowAnonymous]
public class LoginModel(ISignInService signIn, ICurrentUser currentUser) : PageModel
{
    /// <summary>Where to go after signing in. Only ever used through <see cref="PageExtensions.SafeReturnUrl"/>.</summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    public IActionResult OnGet()
    {
        // Someone who is already signed in has no business here.
        return currentUser.IsAuthenticated ? LocalRedirect(this.SafeReturnUrl(ReturnUrl)) : Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await signIn.SignInAsync(Input.UserName, Input.Password, Input.RememberMe, HttpContext.RequestAborted);
        switch (result.Outcome)
        {
            case LoginOutcome.Succeeded:
                return LocalRedirect(this.SafeReturnUrl(ReturnUrl));

            case LoginOutcome.LockedOut:
                var minutes = result.LockoutEnd is { } end ? Math.Max(1, (int)Math.Ceiling((end - DateTimeOffset.UtcNow).TotalMinutes)) : 15;
                ModelState.AddModelError(string.Empty, $"Too many failed sign-in attempts. This account is locked; try again in {minutes} minute{(minutes == 1 ? "" : "s")}.");
                break;

            case LoginOutcome.Inactive:
                ModelState.AddModelError(string.Empty, "This account has been deactivated. Ask an administrator for help.");
                break;

            default:
                ModelState.AddModelError(string.Empty, "Invalid user name or password.");
                break;
        }

        Input.Password = string.Empty;
        return Page();
    }

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "Enter your user name.")]
        [Display(Name = "User name")]
        public string UserName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter your password.")]
        [DataType(DataType.Password)]
        public string Password { get; set; } = string.Empty;

        [Display(Name = "Keep me signed in")]
        public bool RememberMe { get; set; }
    }
}
