namespace InventoryTracker.Services;

/// <summary>Standard list-page query: free-text search, paging and sorting. Bind with <c>[BindProperty(SupportsGet = true)]</c>.</summary>
public class ListQuery
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    public string? Search { get; set; }

    public int Page { get; set; } = 1;

    public int PageSize { get; set; } = DefaultPageSize;

    public string? Sort { get; set; }

    public bool Desc { get; set; }

    /// <summary>Search text trimmed, or <c>null</c> when empty.</summary>
    public string? SearchTerm => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

    /// <summary>Page clamped to ≥ 1.</summary>
    public int SafePage => Math.Max(1, Page);

    /// <summary>Page size clamped to 1..<see cref="MaxPageSize"/> (falls back to the default when not positive).</summary>
    public int SafePageSize => PageSize <= 0 ? DefaultPageSize : Math.Min(PageSize, MaxPageSize);

    public int Skip => (SafePage - 1) * SafePageSize;

    /// <summary>True when <see cref="Sort"/> equals <paramref name="column"/> (case-insensitive).</summary>
    public bool IsSortedBy(string column) => string.Equals(Sort, column, StringComparison.OrdinalIgnoreCase);
}
