using System.Security.Claims;
using InventoryTracker.Auth;
using Microsoft.AspNetCore.Authorization;

namespace InventoryTracker.Pages.Shared;

/// <summary>
/// A top-level navigation entry. <paramref name="Policy"/> is the authorization policy needed to see it.
/// <paramref name="Available"/> is false while the feature that owns the page is not built yet: the item is then
/// shown as a muted "coming soon" label instead of a link that would 404.
/// </summary>
public sealed record NavItem(string Label, string Href, string Policy, bool Available = true);

public static class NavItems
{
    /// <summary>
    /// Convention: the frontend work item of each feature flips its entry to <c>Available: true</c>
    /// (see AGENTS.md). Admin pages live under /Admin; add sub-pages to the Admin landing page, not here.
    /// </summary>
    public static IReadOnlyList<NavItem> All { get; } =
    [
        new("Dashboard", "/", Policies.CanView),
        new("Products", "/Products", Policies.CanView),
        new("Stock", "/Stock", Policies.CanView),
        new("Suppliers", "/Suppliers", Policies.CanView),
        new("Purchase Orders", "/PurchaseOrders", Policies.CanView, Available: false),
        new("Reports", "/Reports", Policies.CanView, Available: false),
        new("Admin", "/Admin", Policies.RequireAdmin),
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
