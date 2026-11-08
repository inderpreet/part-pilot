using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class PurchaseOrderConfiguration : IEntityTypeConfiguration<PurchaseOrder>
{
    public void Configure(EntityTypeBuilder<PurchaseOrder> builder)
    {
        var statuses = string.Join(", ", Enum.GetNames<PurchaseOrderStatus>().Select(n => $"'{n}'"));

        builder.ToTable("PurchaseOrders", t =>
        {
            t.HasCheckConstraint("CK_PurchaseOrders_Status", $"\"Status\" IN ({statuses})");
        });
        builder.HasKey(p => p.Id);

        builder.Property(p => p.Number).IsRequired().HasMaxLength(PurchaseOrder.NumberMaxLength).UseCollation("NOCASE");
        builder.Property(p => p.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        // DateOnly is stored as TEXT yyyy-MM-dd (EF's default), which sorts and compares correctly.
        builder.Property(p => p.OrderDate).IsRequired();
        builder.Property(p => p.Notes).HasMaxLength(PurchaseOrder.NotesMaxLength);

        // Suppliers are archived, never deleted; an order must never lose its supplier.
        builder.HasOne(p => p.Supplier)
            .WithMany()
            .HasForeignKey(p => p.SupplierId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.CreatedBy)
            .WithMany()
            .HasForeignKey(p => p.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => p.Number).IsUnique();
        builder.HasIndex(p => p.Status);
        builder.HasIndex(p => new { p.SupplierId, p.OrderDate });
        builder.HasIndex(p => p.CreatedById);
    }
}

public class PurchaseOrderLineConfiguration : IEntityTypeConfiguration<PurchaseOrderLine>
{
    public void Configure(EntityTypeBuilder<PurchaseOrderLine> builder)
    {
        builder.ToTable("PurchaseOrderLines", t =>
        {
            t.HasCheckConstraint("CK_PurchaseOrderLines_QuantityOrdered", "\"QuantityOrdered\" > 0");
            t.HasCheckConstraint("CK_PurchaseOrderLines_QuantityReceived", "\"QuantityReceived\" >= 0");
            t.HasCheckConstraint("CK_PurchaseOrderLines_UnitCostCents", "\"UnitCostCents\" >= 0");
        });
        builder.HasKey(l => l.Id);

        // EF always sends the value, so a 0 can never be swapped for a default and slip past the CHECK.
        builder.Property(l => l.QuantityReceived).HasDefaultValue(0).ValueGeneratedNever();

        // Only drafts are deletable, and a draft has no lines that anything else references.
        builder.HasOne(l => l.PurchaseOrder)
            .WithMany(p => p.Lines)
            .HasForeignKey(l => l.PurchaseOrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(l => l.Product)
            .WithMany()
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // One line per product per order; also serves "lines of this order".
        builder.HasIndex(l => new { l.PurchaseOrderId, l.ProductId }).IsUnique();

        // "Open orders for this product".
        builder.HasIndex(l => l.ProductId);
    }
}

public class DocumentSequenceConfiguration : IEntityTypeConfiguration<DocumentSequence>
{
    public void Configure(EntityTypeBuilder<DocumentSequence> builder)
    {
        builder.ToTable("DocumentSequences", t =>
        {
            t.HasCheckConstraint("CK_DocumentSequences_NextValue", "\"NextValue\" >= 1");
        });

        // One counter per series per year, so numbering restarts each January.
        builder.HasKey(s => new { s.Name, s.Year });
        builder.Property(s => s.Name).HasMaxLength(DocumentSequence.NameMaxLength);
        builder.Property(s => s.NextValue).HasDefaultValue(1).ValueGeneratedNever();
    }
}
