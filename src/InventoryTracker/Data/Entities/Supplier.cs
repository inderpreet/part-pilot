namespace InventoryTracker.Data.Entities;

/// <summary>
/// Someone the business buys from. Never deleted (purchase orders reference it): archive with <see cref="IsActive"/>
/// instead. An archived supplier cannot be chosen for new purchase orders or new product links.
/// </summary>
public class Supplier : IHasTimestamps, IHasRowVersion
{
    public const int NameMaxLength = 200;
    public const int ContactNameMaxLength = 100;
    public const int EmailMaxLength = 200;
    public const int PhoneMaxLength = 50;
    public const int AddressMaxLength = 500;
    public const int WebsiteMaxLength = 200;
    public const int NotesMaxLength = 2000;

    public int Id { get; set; }

    /// <summary>Unique case-insensitively across active and archived suppliers.</summary>
    public required string Name { get; set; }

    public string? ContactName { get; set; }

    public string? Email { get; set; }

    public string? Phone { get; set; }

    /// <summary>Multi-line postal address.</summary>
    public string? Address { get; set; }

    /// <summary>Always an absolute http(s) URL when present (the service normalises it), so it is safe to render as a link.</summary>
    public string? Website { get; set; }

    public string? Notes { get; set; }

    public bool IsActive { get; set; } = true;

    public long RowVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    /// <summary>The products this supplier provides.</summary>
    public List<ProductSupplier> Products { get; set; } = [];
}
