using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Requisitions;

/// <summary>The requisition list. "Mine" is the default tab; a Viewer never gets any other, which the service enforces too.</summary>
[Authorize(Policy = Policies.CanRequest)]
public class IndexModel(IRequisitionService requisitions, ICurrentUser currentUser) : PageModel
{
    public RequisitionListQuery Query { get; private set; } = new();

    public PagedResult<RequisitionListItem> Requisitions { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    /// <summary>For the requester filter on the All tab.</summary>
    public IReadOnlyList<RequisitionRequester> Requesters { get; private set; } = [];

    /// <summary>Only used to offer the All tab; the service enforces what each user can read.</summary>
    public bool CanSeeAll => currentUser.CanEdit;

    public bool ShowsEveryone => Query.Tab == RequisitionTab.All;

    /// <summary>True when a search, status, requester or date filter is applied (the tab alone is not a filter).</summary>
    public bool IsFiltered => Query.SearchTerm is not null || Query.Status is not null || !string.IsNullOrEmpty(Query.RequesterId) || Query.From is not null || Query.To is not null;

    /// <summary>Full page normally; the table block (<c>#results</c>) for htmx filter / sort / paging requests.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] RequisitionListQuery query)
    {
        var actor = currentUser.ToRequisitionActor();
        if (!CanSeeAll)
        {
            query.Tab = RequisitionTab.Mine;
        }

        (query.FromUtc, query.ToUtcExclusive) = LocalTimeInput.DayRange(query.From, query.To, query.Tz);

        Query = query;
        Requisitions = await requisitions.SearchAsync(query, actor, HttpContext.RequestAborted);
        Requesters = await requisitions.GetRequestersAsync(actor, HttpContext.RequestAborted);

        return this.IsHtmx() ? Partial("_RequisitionResults", this) : Page();
    }
}
