namespace InventoryTracker.Data.Entities;

/// <summary>What an import loads. Stored as its name (text) in <see cref="ImportBatch.Kind"/>.</summary>
public enum ImportKind
{
    /// <summary>Parts, created or updated by SKU.</summary>
    Products,

    /// <summary>Suppliers, created or updated by name.</summary>
    Suppliers,

    /// <summary>Part-supplier links (SKU + supplier name).</summary>
    ProductSuppliers,

    /// <summary>Opening stock or a stock count: posts <see cref="StockTransactionType.Count"/> movements.</summary>
    OpeningStock,

    /// <summary>BOMs: projects and their bill-of-materials lines.</summary>
    Boms,
}

/// <summary>
/// A committed CSV import (F07 FR-5). One row per import, with the counts the Admin saw in the summary; stock movements
/// the import posted point back at it through <see cref="StockTransaction.ImportBatchId"/>. Never edited or deleted.
/// </summary>
public class ImportBatch : IHasCreatedAt
{
    public const int FileNameMaxLength = 255;
    public const int OptionsMaxLength = 200;

    public long Id { get; set; }

    public ImportKind Kind { get; set; }

    public required string FileName { get; set; }

    /// <summary>Data rows in the file (not counting the header).</summary>
    public int RowCount { get; set; }

    public int CreatedCount { get; set; }

    public int UpdatedCount { get; set; }

    public int SkippedCount { get; set; }

    /// <summary>What was chosen on the upload screen, e.g. <c>mode=Replace;createMissingParts=true</c> for a BOM import.</summary>
    public string? Options { get; set; }

    public required string CreatedById { get; set; }

    public ApplicationUser? CreatedBy { get; set; }

    public DateTime CreatedAt { get; set; }
}
