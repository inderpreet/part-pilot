using InventoryTracker.Data.Entities;
using Microsoft.AspNetCore.Identity;

namespace InventoryTracker.Auth;

/// <summary>Caps password length so a huge input can't be used to burn CPU in the password hasher.</summary>
public sealed class PasswordLengthValidator : IPasswordValidator<ApplicationUser>
{
    public const int MaxLength = 128;

    public Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user, string? password)
        => Task.FromResult(password is { Length: > MaxLength }
            ? IdentityResult.Failed(new IdentityError
            {
                Code = "PasswordTooLong",
                Description = $"Password must be at most {MaxLength} characters.",
            })
            : IdentityResult.Success);
}
