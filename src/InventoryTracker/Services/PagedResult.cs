using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

/// <summary>One page of results plus the totals needed to render a pager.</summary>
public sealed class PagedResult<T>(IReadOnlyList<T> items, int page, int pageSize, int totalCount)
{
    public IReadOnlyList<T> Items { get; } = items;

    public int Page { get; } = page;

    public int PageSize { get; } = pageSize;

    public int TotalCount { get; } = totalCount;

    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public bool HasPrevious => Page > 1;

    public bool HasNext => Page < TotalPages;

    /// <summary>1-based index of the first item on this page (0 when empty).</summary>
    public int FirstItem => Items.Count == 0 ? 0 : ((Page - 1) * PageSize) + 1;

    /// <summary>1-based index of the last item on this page (0 when empty).</summary>
    public int LastItem => Items.Count == 0 ? 0 : FirstItem + Items.Count - 1;

    public static PagedResult<T> Empty(ListQuery query) => new([], query.SafePage, query.SafePageSize, 0);

    /// <summary>Counts and fetches one page. The query must already be filtered and ordered.</summary>
    public static async Task<PagedResult<T>> CreateAsync(IQueryable<T> ordered, ListQuery query, CancellationToken cancellationToken = default)
    {
        var total = await ordered.CountAsync(cancellationToken);
        var pageSize = query.SafePageSize;

        // A page past the end (e.g. after deleting rows) snaps to the last page instead of showing nothing.
        var lastPage = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        var page = Math.Min(query.SafePage, lastPage);

        var items = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PagedResult<T>(items, page, pageSize, total);
    }
}
