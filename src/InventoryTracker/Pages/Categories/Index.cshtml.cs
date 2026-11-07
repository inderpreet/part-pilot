using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Categories;

/// <summary>A row of the categories table plus whether to show its edit actions.</summary>
public sealed record CategoryRowModel(CategoryListItem Item, bool CanEdit);

/// <summary>Values for the create / inline-edit form. <c>Id</c> null = new category.</summary>
public sealed record CategoryFormModel(int? Id, string? Name, string? Description);

/// <summary>
/// Category management, all on one page: the list plus inline create / edit / archive through htmx. Anyone signed in may
/// view; the handlers that change data re-check <c>CanEdit</c> because the page-level policy is only <c>CanView</c>.
/// </summary>
[Authorize(Policy = Policies.CanView)]
public class IndexModel(ICategoryService categories, ICurrentUser currentUser) : PageModel
{
    public IReadOnlyList<CategoryListItem> Items { get; private set; } = [];

    /// <summary>Only used to hide edit controls; every data-changing handler enforces <c>CanEdit</c> itself.</summary>
    public bool CanEdit => currentUser.CanEdit;

    public async Task OnGetAsync()
        => Items = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);

    /// <summary>Empty "new category" form, loaded into <c>#category-new</c>.</summary>
    public async Task<IActionResult> OnGetNewAsync()
        => await this.CanEditAsync() ? Partial("_CategoryForm", new CategoryFormModel(null, null, null)) : Forbid();

    /// <summary>The row switched into edit mode.</summary>
    public async Task<IActionResult> OnGetEditAsync(int id)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var item = await categories.GetAsync(id, HttpContext.RequestAborted);
        return item is null ? NotFound() : Partial("_CategoryEditRow", new CategoryFormModel(item.Id, item.Name, item.Description));
    }

    /// <summary>The plain row again (Cancel on an inline edit).</summary>
    public async Task<IActionResult> OnGetRowAsync(int id)
    {
        var item = await categories.GetAsync(id, HttpContext.RequestAborted);
        return item is null ? NotFound() : Partial("_CategoryRow", new CategoryRowModel(item, CanEdit));
    }

    /// <summary>
    /// Creates (no id) or updates a category. Update swaps just the row. Create re-renders the whole section (a new row
    /// lands in sorted position), via <c>HX-Retarget</c>. Failures come back as 422 with the form and its errors.
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(int? id, string? name, string? description)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var input = new CategoryInput(name, description);
        var form = new CategoryFormModel(id, name, description);

        Result<CategoryListItem> result;
        if (id is null)
        {
            result = await categories.CreateAsync(input, HttpContext.RequestAborted);
        }
        else
        {
            if (await categories.GetAsync(id.Value, HttpContext.RequestAborted) is null)
            {
                return NotFound();
            }

            result = await categories.UpdateAsync(id.Value, input, HttpContext.RequestAborted);
        }

        if (!result.Succeeded)
        {
            ModelState.AddErrors(result);
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Partial(id is null ? "_CategoryForm" : "_CategoryEditRow", form);
        }

        this.Flash(id is null ? $"Category '{result.Value.Name}' created." : $"Category '{result.Value.Name}' saved.");

        if (!this.IsHtmx())
        {
            return RedirectToPage();
        }

        if (id is not null)
        {
            return Partial("_CategoryRow", new CategoryRowModel(result.Value, CanEdit: true));
        }

        Response.Headers["HX-Retarget"] = "#category-section";
        Response.Headers["HX-Reswap"] = "innerHTML";
        Items = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        return Partial("_CategorySection", this);
    }

    /// <summary>Archives or restores a category (reference data is never hard-deleted) and returns its refreshed row.</summary>
    public async Task<IActionResult> OnPostSetActiveAsync(int id, bool active)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var result = await categories.SetActiveAsync(id, active, HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            return result.Errors.Any(e => e.Code == "category.not_found")
                ? NotFound()
                : await RowWithErrorAsync(id, result);
        }

        var item = await categories.GetAsync(id, HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        this.Flash(active ? $"Category '{item.Name}' restored." : $"Category '{item.Name}' archived.");
        return Partial("_CategoryRow", new CategoryRowModel(item, CanEdit: true));
    }

    private async Task<IActionResult> RowWithErrorAsync(int id, Result result)
    {
        this.FlashError(string.Join(" ", result.Errors.Select(e => e.Message)));
        var item = await categories.GetAsync(id, HttpContext.RequestAborted);
        return item is null ? NotFound() : Partial("_CategoryRow", new CategoryRowModel(item, CanEdit: true));
    }
}
