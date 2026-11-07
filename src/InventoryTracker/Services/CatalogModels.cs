namespace InventoryTracker.Services;

/// <summary>Which products a list shows. Archived products are kept for history but hidden from day-to-day use.</summary>
public enum ProductStatus
{
    Active,
    Archived,
    All,
}

/// <summary>Product list query. Sort keys: <c>sku</c>, <c>name</c> (default), <c>onhand</c>, <c>category</c>.</summary>
public class ProductListQuery : ListQuery
{
    public int? CategoryId { get; set; }

    public ProductStatus Status { get; set; } = ProductStatus.Active;
}

/// <summary>One row of the product list.</summary>
public sealed record ProductListItem(
    int Id,
    string Sku,
    string Name,
    string? CategoryName,
    string Unit,
    string? BinLocation,
    int QuantityOnHand,
    int ReorderPoint,
    long UnitCostCents,
    bool IsActive,
    bool IsLowStock);

/// <summary>Everything the detail and edit pages need.</summary>
public sealed record ProductDetail(
    int Id,
    string Sku,
    string Name,
    string? Description,
    int? CategoryId,
    string? CategoryName,
    bool CategoryIsActive,
    string Unit,
    string? Barcode,
    string? BinLocation,
    int ReorderPoint,
    int ReorderQuantity,
    long UnitCostCents,
    int QuantityOnHand,
    bool IsActive,
    bool IsLowStock,
    bool SkuLocked,
    long RowVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Editable product fields (create and update). Text is trimmed by the service; blank optional text becomes null and a
/// blank unit becomes "each". <see cref="UnitCostCents"/> comes from <see cref="MoneyParser"/>.
/// </summary>
public sealed record ProductInput(
    string? Sku,
    string? Name,
    string? Description,
    int? CategoryId,
    string? Unit,
    string? Barcode,
    string? BinLocation,
    int ReorderPoint,
    int ReorderQuantity,
    long UnitCostCents);

/// <summary>A compact product for pickers (stock movements, PO lines).</summary>
public sealed record ProductLookupItem(int Id, string Sku, string Name, string Unit, string? Barcode, int QuantityOnHand, bool IsActive);

/// <summary>Form field names used in <see cref="Error.Field"/> so pages can attach messages to inputs.</summary>
public static class ProductFields
{
    public const string Sku = nameof(Sku);
    public const string Name = nameof(Name);
    public const string Description = nameof(Description);
    public const string CategoryId = nameof(CategoryId);
    public const string Unit = nameof(Unit);
    public const string Barcode = nameof(Barcode);
    public const string BinLocation = nameof(BinLocation);
    public const string ReorderPoint = nameof(ReorderPoint);
    public const string ReorderQuantity = nameof(ReorderQuantity);

    /// <summary>The money input (the form field is text; the service works in cents).</summary>
    public const string UnitCost = nameof(UnitCost);
}

public sealed record CategoryListItem(int Id, string Name, string? Description, bool IsActive, int ProductCount);

public sealed record CategoryInput(string? Name, string? Description);

public static class CategoryFields
{
    public const string Name = nameof(Name);
    public const string Description = nameof(Description);
}
