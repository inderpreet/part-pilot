namespace InventoryTracker.Data.Entities;

/// <summary>
/// A numbering counter: the next value to hand out for a named series in a given year (purchase orders use
/// <c>Name = "PurchaseOrder"</c>, one row per year). Never decremented, so numbers are never reused.
/// </summary>
public class DocumentSequence
{
    public const int NameMaxLength = 50;

    public required string Name { get; set; }

    public int Year { get; set; }

    /// <summary>The number the next document gets; starts at 1.</summary>
    public int NextValue { get; set; } = 1;
}
