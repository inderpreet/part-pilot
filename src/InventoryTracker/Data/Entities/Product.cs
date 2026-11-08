namespace InventoryTracker.Data.Entities;

/// <summary>
/// An item the business stocks. Archived (<see cref="IsActive"/> = false) rather than deleted, because stock
/// transactions and purchase orders reference it.
/// </summary>
public class Product : IHasTimestamps, IHasRowVersion
{
    public const int SkuMaxLength = 40;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 2000;
    public const int UnitMaxLength = 20;
    public const int BarcodeMaxLength = 64;
    public const int BinLocationMaxLength = 50;
    public const string DefaultUnit = "each";

    public int Id { get; set; }

    /// <summary>Business-chosen stock keeping unit; unique case-insensitively across active and archived products.</summary>
    public required string Sku { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int? CategoryId { get; set; }

    public Category? Category { get; set; }

    /// <summary>How the product is counted, e.g. "each", "box".</summary>
    public string Unit { get; set; } = DefaultUnit;

    /// <summary>Optional scanner code; unique case-insensitively when present. Never an empty string - use null.</summary>
    public string? Barcode { get; set; }

    /// <summary>Free-text shelf/bin position (single location in v1).</summary>
    public string? BinLocation { get; set; }

    public int ReorderPoint { get; set; }

    public int ReorderQuantity { get; set; }

    /// <summary>Cost per unit in minor currency units (cents).</summary>
    public long UnitCostCents { get; set; }

    /// <summary>
    /// Cached sum of the stock ledger. Written only by <c>StockService</c> (F03); the catalog displays it but never edits it.
    /// </summary>
    public int QuantityOnHand { get; set; }

    public bool IsActive { get; set; } = true;

    public long RowVersion { get; set; }

    /// <summary>The product's stock ledger (F03), oldest first by posting order.</summary>
    public List<StockTransaction> StockTransactions { get; set; } = [];

    /// <summary>The suppliers that provide this product (F04).</summary>
    public List<ProductSupplier> Suppliers { get; set; } = [];

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
