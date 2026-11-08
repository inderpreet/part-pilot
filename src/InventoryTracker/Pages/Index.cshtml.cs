using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages;

/// <summary>The home dashboard (F06 FR-1, FR-2): headline numbers and three short lists. Read-only, so <c>CanView</c>.</summary>
[Authorize(Policy = Policies.CanView)]
public class IndexModel(IDashboardService dashboard, ICurrentUser currentUser, IClock clock) : PageModel
{
    public DashboardSummary Summary { get; private set; } = null!;

    /// <summary>The local calendar date, to flag orders that are due today.</summary>
    public DateOnly Today { get; } = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());

    public bool CanEdit => currentUser.CanEdit;

    /// <summary>A database with nothing in it yet: the page then points at the first thing to do rather than showing zeros.</summary>
    public bool IsEmpty => Summary.Tiles.ActiveSkus == 0 && Summary.RecentMovements.Count == 0 && Summary.Tiles.OpenPurchaseOrders == 0;

    /// <summary>Full page normally; just the dashboard block (<c>#dashboard</c>) for the 60-second htmx refresh.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        Summary = await dashboard.GetAsync(HttpContext.RequestAborted);
        return this.IsHtmx() ? Partial("_Dashboard", this) : Page();
    }
}
