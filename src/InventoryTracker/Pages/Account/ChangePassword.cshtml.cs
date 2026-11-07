using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Account;

[Authorize(Policy = Policies.CanView)]
public class ChangePasswordModel(IUserService users, ISignInService signIn, ICurrentUser currentUser) : PageModel
{
    [BindProperty]
    public PasswordInput Input { get; set; } = new();

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var userId = currentUser.Id!;
        var result = await users.ChangeOwnPasswordAsync(userId, Input.CurrentPassword, Input.NewPassword, HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ModelState.AddErrors(result, nameof(Input));
            return Page();
        }

        // Changing the password rotates the security stamp, which would end this very session at the next check.
        // Re-issue the cookie so the user stays signed in here; their other sessions end.
        await signIn.RefreshSignInAsync(userId);
        this.Flash("Your password has been changed.");
        return RedirectToPage("/Index");
    }

    public sealed class PasswordInput
    {
        [Required(ErrorMessage = "Enter your current password.")]
        [DataType(DataType.Password)]
        [Display(Name = "Current password")]
        public string CurrentPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Enter a new password."), DataType(DataType.Password)]
        [StringLength(PasswordLengthValidator.MaxLength, MinimumLength = 8, ErrorMessage = "The new password must be 8 to 128 characters.")]
        [Display(Name = "New password")]
        public string NewPassword { get; set; } = string.Empty;

        [Required(ErrorMessage = "Confirm the new password."), DataType(DataType.Password)]
        [Compare(nameof(NewPassword), ErrorMessage = "The passwords do not match.")]
        [Display(Name = "Confirm new password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
