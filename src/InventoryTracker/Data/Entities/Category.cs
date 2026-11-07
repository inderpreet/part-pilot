namespace InventoryTracker.Data.Entities;

/// <summary>
/// Flat product classification. Categories are never deleted (products reference them): archive with
/// <see cref="IsActive"/> instead. Archiving leaves the category assigned to its products.
/// </summary>
public class Category : IHasTimestamps
{
    public const int NameMaxLength = 100;
    public const int DescriptionMaxLength = 500;

    public int Id { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<Product> Products { get; set; } = [];
}
