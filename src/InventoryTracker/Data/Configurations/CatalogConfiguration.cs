using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        builder.ToTable("Categories");
        builder.HasKey(c => c.Id);

        // NOCASE folds ASCII only - acceptable for v1 names; revisit if non-ASCII uniqueness matters.
        builder.Property(c => c.Name).IsRequired().HasMaxLength(Category.NameMaxLength).UseCollation("NOCASE");
        builder.Property(c => c.Description).HasMaxLength(Category.DescriptionMaxLength);
        builder.Property(c => c.IsActive).IsRequired();

        builder.HasIndex(c => c.Name).IsUnique();
    }
}

public class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        builder.ToTable("Products", t =>
        {
            t.HasCheckConstraint("CK_Products_ReorderPoint", "\"ReorderPoint\" >= 0");
            t.HasCheckConstraint("CK_Products_ReorderQuantity", "\"ReorderQuantity\" >= 0");
            t.HasCheckConstraint("CK_Products_UnitCostCents", "\"UnitCostCents\" >= 0");
        });
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Sku).IsRequired().HasMaxLength(Product.SkuMaxLength).UseCollation("NOCASE");
        // NOCASE so ORDER BY Name is case-insensitive and can use the Name indexes.
        builder.Property(p => p.Name).IsRequired().HasMaxLength(Product.NameMaxLength).UseCollation("NOCASE");
        builder.Property(p => p.Description).HasMaxLength(Product.DescriptionMaxLength);
        builder.Property(p => p.Unit).IsRequired().HasMaxLength(Product.UnitMaxLength).HasDefaultValue(Product.DefaultUnit);
        builder.Property(p => p.Barcode).HasMaxLength(Product.BarcodeMaxLength).UseCollation("NOCASE");
        builder.Property(p => p.BinLocation).HasMaxLength(Product.BinLocationMaxLength);
        builder.Property(p => p.QuantityOnHand).HasDefaultValue(0);
        builder.Property(p => p.IsActive).IsRequired();

        builder.HasOne(p => p.Category)
            .WithMany(c => c.Products)
            .HasForeignKey(p => p.CategoryId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.Sku).IsUnique();
        builder.HasIndex(p => p.Barcode).IsUnique().HasFilter("\"Barcode\" IS NOT NULL");
        builder.HasIndex(p => p.Name);
        builder.HasIndex(p => p.CategoryId);
        builder.HasIndex(p => new { p.IsActive, p.Name });
    }
}
