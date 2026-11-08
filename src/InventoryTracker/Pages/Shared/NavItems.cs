using System.Security.Claims;
using InventoryTracker.Auth;
using Microsoft.AspNetCore.Authorization;

namespace InventoryTracker.Pages.Shared;

/// <summary>
/// A top-level navigation entry. <paramref name="Policy"/> is the authorization policy needed to see it.
/// <paramref name="Available"/> is false while the feature that owns the page is not built yet: the item is then
/// shown as a muted "coming soon" label instead of a link that would 404.
/// <paramref name="Group"/> is the rail section it sits under (<see cref="NavGroups"/>); <c>null</c> means the top of the rail.
/// <paramref name="Icon"/> is the id of a symbol in <c>_Icons.cshtml</c> (without the <c>i-</c> prefix).
/// </summary>
public sealed record NavItem(string Label, string Href, string Policy, bool Available = true, string? Group = null, string Icon = "home");

/// <summary>Rail sections. <see cref="Foot"/> items sit at the bottom of the rail, above the user card.</summary>
public static class NavGroups
{
    public const string Inventory = "Inventory";
    public const string Insights = "Insights";
    public const string Foot = "Foot";
}

public static class NavItems
{
    /// <summary>
    /// Convention: the frontend work item of each feature flips its entry to <c>Available: true</c>
    /// (see AGENTS.md). Admin pages live under /Admin; add sub-pages to the Admin landing page, not here.
    /// </summary>
    public static IReadOnlyList<NavItem> All { get; } =
    [
        new("Dashboard", "/", Policies.CanView, Icon: "home"),
        new("Products", "/Products", Policies.CanView, Group: NavGroups.Inventory, Icon: "box"),
        new("Stock", "/Stock", Policies.CanView, Group: NavGroups.Inventory, Icon: "layers"),
        new("Suppliers", "/Suppliers", Policies.CanView, Group: NavGroups.Inventory, Icon: "users"),
        new("Purchase Orders", "/PurchaseOrders", Policies.CanView, Group: NavGroups.Inventory, Icon: "file"),
        new("Reports", "/Reports", Policies.CanView, Available: false, Group: NavGroups.Insights, Icon: "chart"),
        new("Admin", "/Admin", Policies.RequireAdmin, Group: NavGroups.Foot, Icon: "gear"),
    ];

    /// <summary>The items <paramref name="user"/> is allowed to see, by evaluating each item's authorization policy.</summary>
    public static async Task<IReadOnlyList<NavItem>> VisibleToAsync(IAuthorizationService authorization, ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true)
        {
            return [];
        }

        var visible = new List<NavItem>();
        foreach (var item in All)
        {
            if ((await authorization.AuthorizeAsync(user, item.Policy)).Succeeded)
            {
                visible.Add(item);
            }
        }

        return visible;
    }

    /// <summary>Whether <paramref name="item"/> is the section containing <paramref name="path"/>.</summary>
    public static bool IsCurrent(NavItem item, string path)
        => item.Href == "/"
            ? path == "/"
            : path.Equals(item.Href, StringComparison.OrdinalIgnoreCase)
              || path.StartsWith(item.Href + "/", StringComparison.OrdinalIgnoreCase);
}
