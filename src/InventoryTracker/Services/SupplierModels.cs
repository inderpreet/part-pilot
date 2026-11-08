namespace InventoryTracker.Services;

/// <summary>Which suppliers a list shows. Archived suppliers are kept for history but hidden from day-to-day use.</summary>
public enum SupplierStatus
{
    Active,
    Archived,
    All,
}

/// <summary>Supplier list query. Search matches name, contact name and email. Sort keys: <c>name</c> (default), <c>contact</c>, <c>products</c>.</summary>
public class SupplierListQuery : ListQuery
{
    public SupplierStatus Status { get; set; } = SupplierStatus.Active;
}

/// <summary>One row of the supplier list.</summary>
public sealed record SupplierListItem(
    int Id,
    string Name,
    string? ContactName,
    string? Phone,
    string? Email,
    bool IsActive,
    int ProductCount);

/// <summary>Everything the supplier detail and edit pages need.</summary>
public sealed record SupplierDetail(
    int Id,
    string Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? Address,
    string? Website,
    string? Notes,
    bool IsActive,
    int ProductCount,
    long RowVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt);

/// <summary>
/// Editable supplier fields. Text is trimmed by the service and blank optional text becomes null. A website typed
/// without a scheme ("acme.example") is stored as <c>https://acme.example</c>.
/// </summary>
public sealed record SupplierInput(
    string? Name,
    string? ContactName,
    string? Email,
    string? Phone,
    string? Address,
    string? Website,
    string? Notes);

/// <summary>
/// The editable part of a product–supplier link. <see cref="IsPreferred"/> = true makes this the product's preferred
/// supplier (and clears the flag on its other links); false clears it here.
/// </summary>
public sealed record SupplierLinkInput(
    string? SupplierSku,
    long UnitCostCents,
    int? LeadTimeDays,
    int MinOrderQuantity,
    bool IsPreferred);

/// <summary>One product–supplier link with both sides' names, so the same row renders on the supplier page and on the product page.</summary>
public sealed record ProductSupplierItem(
    int ProductId,
    string Sku,
    string ProductName,
    bool ProductIsActive,
    int SupplierId,
    string SupplierName,
    bool SupplierIsActive,
    string? SupplierSku,
    long UnitCostCents,
    int? LeadTimeDays,
    int MinOrderQuantity,
    bool IsPreferred);

/// <summary>A compact supplier for pickers and selects (PO supplier choice, "add supplier to product").</summary>
public sealed record SupplierLookupItem(int Id, string Name, string? ContactName, bool IsActive);

/// <summary>Form field names used in <see cref="Error.Field"/> so pages can attach messages to inputs.</summary>
public static class SupplierFields
{
    public const string Name = nameof(Name);
    public const string ContactName = nameof(ContactName);
    public const string Email = nameof(Email);
    public const string Phone = nameof(Phone);
    public const string Address = nameof(Address);
    public const string Website = nameof(Website);
    public const string Notes = nameof(Notes);
}

/// <summary>Field names for product–supplier link errors.</summary>
public static class SupplierLinkFields
{
    public const string ProductId = nameof(ProductId);
    public const string SupplierId = nameof(SupplierId);
    public const string SupplierSku = nameof(SupplierSku);

    /// <summary>The money input (the form field is text; the service works in cents).</summary>
    public const string UnitCost = nameof(UnitCost);
    public const string LeadTimeDays = nameof(LeadTimeDays);
    public const string MinOrderQuantity = nameof(MinOrderQuantity);
}
