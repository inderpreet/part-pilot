namespace InventoryTracker.Data.Entities;

/// <summary>
/// "Supplier X provides product Y": the supplier's own SKU, what they charge, how long they take. The pair
/// (<see cref="ProductId"/>, <see cref="SupplierId"/>) is the key. Removing a link deletes the row; purchase orders keep
/// their own copies of what they need, so they are unaffected.
/// </summary>
public class ProductSupplier : IHasTimestamps
{
    public const int SupplierSkuMaxLength = 64;
    public const int MaxLeadTimeDays = 3650;
    public const int MaxMinOrderQuantity = 1_000_000_000;

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public int SupplierId { get; set; }

    public Supplier? Supplier { get; set; }

    /// <summary>How the supplier refers to the product; null when it is the same as ours.</summary>
    public string? SupplierSku { get; set; }

    /// <summary>The supplier's price per unit in minor currency units (cents).</summary>
    public long UnitCostCents { get; set; }

    public int? LeadTimeDays { get; set; }

    public int MinOrderQuantity { get; set; } = 1;

    /// <summary>At most one link per product has this set (a filtered unique index enforces it).</summary>
    public bool IsPreferred { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
