namespace InventoryTracker.Services;

/// <summary>Builds <c>LIKE</c> patterns from user-typed search text.</summary>
public static class LikePattern
{
    /// <summary>The escape character to pass as the third argument of <c>EF.Functions.Like</c>.</summary>
    public const string Escape = "\\";

    /// <summary>
    /// A "contains" pattern for <paramref name="term"/>. The LIKE wildcards <c>%</c> and <c>_</c> (and the escape
    /// character itself) in the term are matched literally, so searching for "50%" does not match everything.
    /// </summary>
    public static string Contains(string term) =>
        "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
}
