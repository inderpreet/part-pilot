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

    /// <summary>A line's decision in words; an approval of less than was asked for reads "Reduced".</summary>
    public static string OutcomeLabel(RequisitionLineItem line) => line.Outcome switch
    {
        null => "—",
        { Decision: RequisitionDecision.Rejected } => "Rejected",
        { QuantityApproved: var quantity } when quantity < line.QuantityRequested => "Reduced",
        _ => "Approved",
    };

    public static string OutcomeCss(RequisitionLineItem line) => OutcomeLabel(line) switch
    {
        "Approved" => "badge-received",
        "Reduced" => "badge-submitted",
        "Rejected" => "badge-cancelled",
        _ => "badge-draft",
    };

    public static string OrderStateLabel(RequisitionOrderState state) => state switch
    {
        RequisitionOrderState.Ordered => "Ordered",
        RequisitionOrderState.PartlyOrdered => "Partly ordered",
        _ => "Not ordered",
    };

    public static string OrderStateCss(RequisitionOrderState state) => state switch
    {
        RequisitionOrderState.Ordered => "badge-received",
        RequisitionOrderState.PartlyOrdered => "badge-partial",
        _ => "badge-draft",
    };

    public static string TabLabel(RequisitionTab tab) => tab switch
    {
        RequisitionTab.Mine => "Mine",
        RequisitionTab.All => "All",
        _ => tab.ToString(),
    };

    /// <summary>Admin, Manager and Staff read everyone's requisitions; only an Admin may change someone else's; Admin and Manager decide.</summary>
    public static RequisitionActor ToRequisitionActor(this ICurrentUser user)
        => new(user.Id!, CanSeeAll: user.CanEdit, IsAdmin: user.IsAdmin, CanApprove: user.CanApprove, CanOrder: user.CanEdit);
}
