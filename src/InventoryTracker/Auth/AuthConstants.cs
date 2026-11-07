namespace InventoryTracker.Auth;

/// <summary>Authorization policy names. Every page declares one via <c>[Authorize(Policy = ...)]</c>.</summary>
public static class Policies
{
    /// <summary>Admin only: user management, settings.</summary>
    public const string RequireAdmin = nameof(RequireAdmin);

    /// <summary>Admin or Staff: anything that changes data.</summary>
    public const string CanEdit = nameof(CanEdit);

    /// <summary>Any signed-in, active user (Admin, Staff, Viewer).</summary>
    public const string CanView = nameof(CanView);
}

/// <summary>Custom claim types written into the auth cookie.</summary>
public static class AppClaimTypes
{
    public const string DisplayName = "inv:display_name";

    /// <summary>"true" while the account is active. Required by every policy, so deactivation locks the user out.</summary>
    public const string Active = "inv:active";
}

/// <summary>Auth-related configuration keys (all optional).</summary>
public static class AuthConfigKeys
{
    /// <summary>Seconds between re-checks of the user's security stamp / active flag (default 60). Tests set 0.</summary>
    public const string ValidationIntervalSeconds = "Auth:ValidationIntervalSeconds";
}
