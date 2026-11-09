using InventoryTracker.Data;
using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

public enum ImportRowAction
{
    Create,
    Update,
    Skip,
    Error,
}

public enum BomImportMode
{
    Merge,
    Replace,
}

/// <summary>Choices on the upload screen. Only BOM import uses them today.</summary>
public sealed record ImportOptions(BomImportMode Mode = BomImportMode.Merge, bool CreateMissingParts = false)
{
    public string ToStored() => $"mode={Mode};createMissingParts={(CreateMissingParts ? "true" : "false")}";
}

/// <summary>One preview row shown before commit.</summary>
public sealed record ImportPreviewRow(int LineNumber, ImportRowAction Action, string Key, string Summary, string? Error, string? Project = null);

/// <summary>Lines a BOM Replace will remove from a project that already exists.</summary>
public sealed record BomReplacePreview(string ProjectNumber, string ProjectName, int RemovedCount, IReadOnlyList<string> RemovedSkus);

/// <summary>Project fields a BOM import will change on a project that already exists.</summary>
public sealed record BomHeaderChangePreview(string ProjectNumber, string ProjectName, IReadOnlyList<string> Changes);

public sealed record ImportPreview(
    string Token,
    ImportKind Kind,
    string FileName,
    ImportOptions Options,
    int RowCount,
    int CreateCount,
    int UpdateCount,
    int SkipCount,
    int ErrorCount,
    IReadOnlyList<ImportPreviewRow> Rows,
    IReadOnlyList<BomReplacePreview> Replacements,
    IReadOnlyList<BomHeaderChangePreview> ProjectChanges);

public sealed record ImportBatchResult(
    long Id,
    ImportKind Kind,
    string FileName,
    int RowCount,
    int CreatedCount,
    int UpdatedCount,
    int SkippedCount,
    string? Options,
    string CreatedByName,
    DateTime CreatedAt);

public sealed record ImportActor(string UserId, bool IsAdmin);

public static class ImportKinds
{
    public static IReadOnlyList<string> ProductHeaders { get; } =
        ["sku", "name", "description", "category", "unit", "barcode", "bin_location", "reorder_point", "reorder_quantity", "unit_cost", "active"];

    public static IReadOnlyList<string> ProductRequired { get; } = ["sku", "name"];

    public static IReadOnlyList<string> SupplierHeaders { get; } =
        ["name", "contact_name", "email", "phone", "address", "website", "notes", "active"];

    public static IReadOnlyList<string> SupplierRequired { get; } = ["name"];

    public static IReadOnlyList<string> ProductSupplierHeaders { get; } =
        ["sku", "supplier", "supplier_sku", "unit_cost", "lead_time_days", "min_order_quantity", "preferred"];

    public static IReadOnlyList<string> ProductSupplierRequired { get; } = ["sku", "supplier"];

    public static IReadOnlyList<string> OpeningStockHeaders { get; } = ["sku", "quantity", "note"];

    public static IReadOnlyList<string> OpeningStockRequired { get; } = ["sku", "quantity"];

    public static IReadOnlyList<string> BomHeaders { get; } =
        ["project_number", "project_name", "target_units", "description", "notes", "sku", "quantity_per_unit", "note", "part_name", "part_unit", "part_unit_cost"];

    public static IReadOnlyList<string> BomRequired { get; } = ["project_name"];

    public static IReadOnlyList<string> RequiredHeaders(ImportKind kind) => kind switch
    {
        ImportKind.Products => ProductRequired,
        ImportKind.Suppliers => SupplierRequired,
        ImportKind.ProductSuppliers => ProductSupplierRequired,
        ImportKind.OpeningStock => OpeningStockRequired,
        ImportKind.Boms => BomRequired,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static string TemplateFileName(ImportKind kind) => kind switch
    {
        ImportKind.Products => "products.csv",
        ImportKind.Suppliers => "suppliers.csv",
        ImportKind.ProductSuppliers => "product_suppliers.csv",
        ImportKind.OpeningStock => "opening_stock.csv",
        ImportKind.Boms => "bom.csv",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public static DataPermission Permission(ImportKind kind) => kind is ImportKind.Boms
        ? DataPermission.ImportBoms
        : DataPermission.ImportData;

    public static string Label(ImportKind kind) => kind switch
    {
        ImportKind.Products => "Parts",
        ImportKind.Suppliers => "Suppliers",
        ImportKind.ProductSuppliers => "Part–supplier links",
        ImportKind.OpeningStock => "Opening stock",
        ImportKind.Boms => "BOMs",
        _ => kind.ToString(),
    };
}
