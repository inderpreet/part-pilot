using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Admin.Users;

/// <summary>Creates a user (no id) or edits one. The user name is fixed once created.</summary>
[Authorize(Policy = Policies.RequireAdmin)]
public class EditModel(IUserService users, ICurrentUser currentUser) : PageModel
{
    [BindProperty]
    public UserInput Input { get; set; } = new();

    public UserDto? Existing { get; private set; }

    public bool IsEdit => Existing is not null;

    public bool IsSelf => Existing?.Id == currentUser.Id;

    public async Task<IActionResult> OnGetAsync(string? id)
    {
        if (id is null)
        {
            return Page();
        }

        Existing = await users.GetAsync(id, HttpContext.RequestAborted);
        if (Existing is null)
        {
            return NotFound();
        }

        Input = new UserInput
        {
            DisplayName = Existing.DisplayName,
            Email = Existing.Email,
            Role = Existing.Role ?? Roles.Viewer,
            IsActive = Existing.IsActive,
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? id)
    {
        if (id is not null)
        {
            Existing = await users.GetAsync(id, HttpContext.RequestAborted);
            if (Existing is null)
            {
                return NotFound();
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        if (Existing is null)
        {
            var created = await users.CreateAsync(
                new CreateUserInput(Input.UserName ?? string.Empty, Input.DisplayName, Input.Email, Input.Role, Input.Password ?? string.Empty, Input.IsActive),
                currentUser.Id,
                HttpContext.RequestAborted);
            if (!created.Succeeded)
            {
                ModelState.AddErrors(created, nameof(Input));
                return Page();
            }

            this.Flash($"User '{created.Value.UserName}' created.");
            return RedirectToPage("Index");
        }

        var updated = await users.UpdateAsync(
            new UpdateUserInput(Existing.Id, Input.DisplayName, Input.Email, Input.Role, Input.IsActive),
            currentUser.Id,
            HttpContext.RequestAborted);
        if (!updated.Succeeded)
        {
            ModelState.AddErrors(updated, nameof(Input));
            return Page();
        }

        this.Flash($"User '{Existing.UserName}' saved.");
        return RedirectToPage("Index");
    }

    /// <summary>The reset-password dialog, loaded into <c>#modal</c> by htmx.</summary>
    public async Task<IActionResult> OnGetResetPasswordAsync(string id)
    {
        var user = await users.GetAsync(id, HttpContext.RequestAborted);
        return user is null ? NotFound() : Partial("_ResetPasswordModal", user);
    }

    /// <summary>Success closes the dialog and flashes; validation problems come back as 422 and are shown inside the dialog.</summary>
    public async Task<IActionResult> OnPostResetPasswordAsync(string id, [FromForm] string? newPassword, [FromForm] string? confirmPassword)
    {
        var user = await users.GetAsync(id, HttpContext.RequestAborted);
        if (user is null)
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(newPassword))
        {
            ModelState.AddModelError(UserFields.NewPassword, "Enter a new password.");
        }
        else if (newPassword != confirmPassword)
        {
            ModelState.AddModelError(UserFields.NewPassword, "The passwords do not match.");
        }
        else
        {
            var result = await users.ResetPasswordAsync(id, newPassword, currentUser.Id, HttpContext.RequestAborted);
            if (result.Succeeded)
            {
                Response.HxTrigger("flash", $"Password reset for '{user.UserName}'.");
                Response.HxTrigger("closeModal");
                return new NoContentResult();
            }

            ModelState.AddErrors(result);
        }

        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Partial("_ValidationSummary", ModelState);
    }

    public sealed class UserInput
    {
        /// <summary>Create only. Validated by the service (length, allowed characters, uniqueness).</summary>
        [Display(Name = "User name")]
        public string? UserName { get; set; }

        [Required(ErrorMessage = "Display name is required."), StringLength(ApplicationUser.DisplayNameMaxLength)]
        [Display(Name = "Display name")]
        public string DisplayName { get; set; } = string.Empty;

        [EmailAddress(ErrorMessage = "Enter a valid email address or leave it blank."), StringLength(256)]
        public string? Email { get; set; }

        [Required(ErrorMessage = "Choose a role.")]
        public string Role { get; set; } = Roles.Staff;

        public bool IsActive { get; set; } = true;

        /// <summary>Create only; an existing user's password is changed with "Reset password".</summary>
        [DataType(DataType.Password), StringLength(PasswordLengthValidator.MaxLength, ErrorMessage = "Password must be at most 128 characters.")]
        public string? Password { get; set; }

        [DataType(DataType.Password), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
        [Display(Name = "Confirm password")]
        public string? ConfirmPassword { get; set; }
    }
}
