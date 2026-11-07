using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Account;

/// <summary>First-run setup: creates the Admin account. Returns 404 as soon as any user exists.</summary>
[AllowAnonymous]
public class SetupModel(
    IFirstRunState firstRun,
    IUserService users,
    ISignInService signIn,
    ISettingsService settings) : PageModel
{
    [BindProperty]
    public SetupInput Input { get; set; } = new();

    public async Task<IActionResult> OnGetAsync()
    {
        if (await firstRun.HasUsersAsync(HttpContext.RequestAborted))
        {
            return NotFound();
        }

        Input.CompanyName = await settings.GetStringAsync(SettingKeys.CompanyName);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (await firstRun.HasUsersAsync(HttpContext.RequestAborted))
        {
            return NotFound();
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var created = await users.CreateFirstAdminAsync(Input.UserName, Input.DisplayName, Input.Email, Input.Password, HttpContext.RequestAborted);
        if (!created.Succeeded)
        {
            // Lost a race with another browser finishing setup first: behave as if the page never existed.
            if (created.Errors.Any(e => e.Code == "setup.complete"))
            {
                return NotFound();
            }

            ModelState.AddErrors(created, nameof(Input));
            return Page();
        }

        var current = await settings.GetBusinessSettingsAsync(HttpContext.RequestAborted);
        var saved = await settings.UpdateBusinessSettingsAsync(current with { CompanyName = Input.CompanyName }, HttpContext.RequestAborted);
        if (!saved.Succeeded)
        {
            // The account exists now; the company name is editable later under Admin > Settings.
            this.Flash("Admin account created, but the company name could not be saved. Set it under Admin > Settings.");
        }
        else
        {
            this.Flash("Welcome! Your admin account is ready.");
        }

        await signIn.SignInAsync(Input.UserName, Input.Password, rememberMe: false, HttpContext.RequestAborted);
        return LocalRedirect("/");
    }

    public sealed class SetupInput
    {
        [Required, StringLength(100)]
        [Display(Name = "Company name")]
        public string CompanyName { get; set; } = string.Empty;

        [Required, StringLength(50)]
        [Display(Name = "Admin user name")]
        public string UserName { get; set; } = string.Empty;

        [Required, StringLength(ApplicationUser.DisplayNameMaxLength)]
        [Display(Name = "Your name")]
        public string DisplayName { get; set; } = string.Empty;

        [EmailAddress, StringLength(256)]
        [Display(Name = "Email (optional)")]
        public string? Email { get; set; }

        [Required, DataType(DataType.Password), StringLength(PasswordLengthValidator.MaxLength, MinimumLength = 8)]
        public string Password { get; set; } = string.Empty;

        [Required, DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [Display(Name = "Confirm password")]
        public string ConfirmPassword { get; set; } = string.Empty;
    }
}
