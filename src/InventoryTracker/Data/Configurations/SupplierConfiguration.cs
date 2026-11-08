using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> builder)
    {
        builder.ToTable("Suppliers");
        builder.HasKey(s => s.Id);

        // NOCASE (ASCII only, like products and categories) so ORDER BY Name is case-insensitive and "Acme" = "ACME".
        builder.Property(s => s.Name).IsRequired().HasMaxLength(Supplier.NameMaxLength).UseCollation("NOCASE");
        builder.Property(s => s.ContactName).HasMaxLength(Supplier.ContactNameMaxLength);
        builder.Property(s => s.Email).HasMaxLength(Supplier.EmailMaxLength);
        builder.Property(s => s.Phone).HasMaxLength(Supplier.PhoneMaxLength);
        builder.Property(s => s.Address).HasMaxLength(Supplier.AddressMaxLength);
        builder.Property(s => s.Website).HasMaxLength(Supplier.WebsiteMaxLength);
        builder.Property(s => s.Notes).HasMaxLength(Supplier.NotesMaxLength);
        builder.Property(s => s.IsActive).IsRequired();

        builder.HasIndex(s => s.Name).IsUnique();
        builder.HasIndex(s => new { s.IsActive, s.Name });
    }
}

public class ProductSupplierConfiguration : IEntityTypeConfiguration<ProductSupplier>
{
    public void Configure(EntityTypeBuilder<ProductSupplier> builder)
    {
        builder.ToTable("ProductSuppliers", t =>
        {
            t.HasCheckConstraint("CK_ProductSuppliers_UnitCostCents", "\"UnitCostCents\" >= 0");
            t.HasCheckConstraint("CK_ProductSuppliers_LeadTimeDays", "\"LeadTimeDays\" IS NULL OR \"LeadTimeDays\" >= 0");
            t.HasCheckConstraint("CK_ProductSuppliers_MinOrderQuantity", "\"MinOrderQuantity\" >= 1");
        });
        builder.HasKey(l => new { l.ProductId, l.SupplierId });

        builder.Property(l => l.SupplierSku).HasMaxLength(ProductSupplier.SupplierSkuMaxLength);
        // The DB default (1) serves raw SQL; EF always sends the value (otherwise EF would swap a 0 for the default and hide the CHECK).
        builder.Property(l => l.MinOrderQuantity).HasDefaultValue(1).ValueGeneratedNever();
        builder.Property(l => l.IsPreferred).IsRequired();

        // Parents are archived, never deleted, so a link must never silently disappear with one.
        builder.HasOne(l => l.Product)
            .WithMany(p => p.Suppliers)
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(l => l.Supplier)
            .WithMany(s => s.Products)
            .HasForeignKey(l => l.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        // At most one preferred supplier per product.
        builder.HasIndex(l => l.ProductId).IsUnique().HasFilter("\"IsPreferred\" = 1").HasDatabaseName("UX_ProductSuppliers_Preferred");

        // "All products of this supplier" (the PK covers the product side).
        builder.HasIndex(l => l.SupplierId);
    }
}
