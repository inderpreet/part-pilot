using System.Security.Claims;
using InventoryTracker.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace InventoryTracker.Auth;

/// <summary>Adds the display name and the "active" marker to the standard Identity claims.</summary>
public sealed class AppClaimsPrincipalFactory(
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> options)
    : UserClaimsPrincipalFactory<ApplicationUser, IdentityRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(ApplicationUser user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(AppClaimTypes.DisplayName, user.DisplayName));

        // An inactive user can still be authenticated by a stale cookie for a moment; without this claim every
        // policy (including the fallback) refuses them.
        if (user.IsActive)
        {
            identity.AddClaim(new Claim(AppClaimTypes.Active, "true"));
        }

        return identity;
    }
}
