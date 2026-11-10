namespace InventoryTracker.Data;

/// <summary>The fixed roles. A user has exactly one.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string Staff = "Staff";
    public const string Viewer = "Viewer";

    /// <summary>All roles, most to least privileged (the order shown in role pickers).</summary>
    public static readonly IReadOnlyList<string> All = [Admin, Manager, Staff, Viewer];

    public static bool IsValid(string? role) => role is not null && All.Contains(role, StringComparer.Ordinal);
}
