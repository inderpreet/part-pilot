using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Admin.Users;

[Authorize(Policy = Policies.RequireAdmin)]
public class IndexModel(IUserService users, ICurrentUser currentUser) : PageModel
{
    public ListQuery Query { get; private set; } = new();

    public PagedResult<UserDto> Users { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    public string? CurrentUserId => currentUser.Id;

    /// <summary>Full page normally; just the results block (<c>#results</c>) for htmx search / sort / paging requests.</summary>
    public async Task<IActionResult> OnGetAsync([FromQuery] ListQuery query)
    {
        Query = query;
        Users = await users.ListAsync(query, HttpContext.RequestAborted);

        return this.IsHtmx() ? Partial("_UserTable", this) : Page();
    }
}
