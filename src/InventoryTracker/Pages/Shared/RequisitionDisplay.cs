using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;

namespace InventoryTracker.Pages.Shared;

/// <summary>How requisition statuses are shown, and who the signed-in user is to the requisition service.</summary>
public static class RequisitionDisplay
{
    public static string StatusLabel(RequisitionStatus status) => status.ToString();

    /// <summary>The <c>badge-*</c> CSS class of a status (site.css).</summary>
    public static string StatusCss(RequisitionStatus status) => status switch
    {
        RequisitionStatus.Draft => "badge-draft",
        RequisitionStatus.Submitted => "badge-submitted",
        RequisitionStatus.Approved => "badge-received",
        RequisitionStatus.Rejected => "badge-cancelled",
        RequisitionStatus.Cancelled => "badge-cancelled",
        _ => "badge-draft",
    };

    public static string TabLabel(RequisitionTab tab) => tab switch
    {
        RequisitionTab.Mine => "Mine",
        RequisitionTab.All => "All",
        _ => tab.ToString(),
    };

    /// <summary>Admin and Staff read everyone's requisitions; only an Admin may change someone else's.</summary>
    public static RequisitionActor ToRequisitionActor(this ICurrentUser user)
        => new(user.Id!, CanSeeAll: user.CanEdit, IsAdmin: user.IsAdmin);
}
