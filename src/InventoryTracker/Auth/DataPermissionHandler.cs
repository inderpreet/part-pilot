using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;

namespace InventoryTracker.Auth;

/// <summary>One or more data permissions; the handler succeeds when the user holds any of them (Admin always does).</summary>
public sealed class DataPermissionRequirement : IAuthorizationRequirement
{
    public DataPermissionRequirement(params DataPermission[] permissions)
    {
        if (permissions.Length == 0)
        {
            throw new ArgumentException("Name at least one permission.", nameof(permissions));
        }

        Permissions = permissions;
    }

    public IReadOnlyList<DataPermission> Permissions { get; }
}

/// <summary>
/// Admin always passes. Manager, Staff and Viewer pass when their setting for one of the named permissions is true.
/// Settings are read live, so a change on the Settings page applies on the next request.
/// </summary>
public sealed class DataPermissionHandler(ISettingsService settings) : AuthorizationHandler<DataPermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, DataPermissionRequirement requirement)
    {
        if (context.User.IsInRole(Roles.Admin))
        {
            context.Succeed(requirement);
            return;
        }

        var role = DataPermissions.ConfigurableRoles.FirstOrDefault(context.User.IsInRole);
        if (role is null)
        {
            return;
        }

        foreach (var permission in requirement.Permissions)
        {
            if (await settings.GetBoolAsync(SettingKeys.Permission(permission, role)))
            {
                context.Succeed(requirement);
                return;
            }
        }
    }
}
