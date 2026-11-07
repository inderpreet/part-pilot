using InventoryTracker.Services;

namespace InventoryTracker.Pages.Shared;

/// <summary>Model for the <c>_Pager</c> partial. Links keep the current query string and replace only <c>page</c>.</summary>
public sealed record PagerModel(int Page, int PageSize, int TotalCount, string Target = "#results")
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public static PagerModel From<T>(PagedResult<T> result, string target = "#results")
        => new(result.Page, result.PageSize, result.TotalCount, target);
}

/// <summary>Model for the <c>_SortHeader</c> partial: a <c>&lt;th&gt;</c> that toggles sorting by <paramref name="Column"/>.</summary>
public sealed record SortHeaderModel(string Column, string Label, ListQuery Query, string Target = "#results");

/// <summary>Model for the <c>_EmptyState</c> partial.</summary>
public sealed record EmptyStateModel(string Title, string? Message = null, string? ActionText = null, string? ActionUrl = null);

/// <summary>Model for the <c>_ConfirmButton</c> partial: a button that asks for confirmation, then <c>hx-post</c>s.</summary>
public sealed record ConfirmButtonModel(
    string Text,
    string Confirm,
    string PostUrl,
    string? Target = null,
    string? Swap = null,
    string CssClass = "secondary");
