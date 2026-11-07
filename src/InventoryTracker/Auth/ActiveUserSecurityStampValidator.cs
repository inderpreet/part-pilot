using System.Security.Claims;
using InventoryTracker.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Auth;

/// <summary>
/// Periodically (see <see cref="SecurityStampValidatorOptions.ValidationInterval"/>) re-checks the signed-in user
/// against the database. In addition to Identity's security-stamp check it rejects deactivated users, so a
/// deactivated user's existing sessions end within one validation interval. Surviving sessions get their claims
/// (role, display name) rebuilt from the database.
/// </summary>
public sealed class ActiveUserSecurityStampValidator(
    IOptions<SecurityStampValidatorOptions> options,
    SignInManager<ApplicationUser> signInManager,
    ILoggerFactory logger)
    : SecurityStampValidator<ApplicationUser>(options, signInManager, logger)
{
    protected override async Task<ApplicationUser?> VerifySecurityStamp(ClaimsPrincipal? principal)
    {
        var user = await base.VerifySecurityStamp(principal);
        if (user is { IsActive: false })
        {
            Logger.LogInformation("Rejecting session of deactivated user {UserId}", user.Id);
            return null;
        }

        return user;
    }
}
