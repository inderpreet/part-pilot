using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class RequisitionConfiguration : IEntityTypeConfiguration<Requisition>
{
    public void Configure(EntityTypeBuilder<Requisition> builder)
    {
        var statuses = string.Join(", ", Enum.GetNames<RequisitionStatus>().Select(n => $"'{n}'"));

        builder.ToTable("Requisitions", t =>
        {
            t.HasCheckConstraint("CK_Requisitions_Status", $"\"Status\" IN ({statuses})");
        });
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Number).IsRequired().HasMaxLength(Requisition.NumberMaxLength).UseCollation("NOCASE");
        builder.Property(r => r.Status).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.Reason).HasMaxLength(Requisition.ReasonMaxLength);

        // A requisition must never lose who asked for it; users are deactivated, not deleted.
        builder.HasOne(r => r.RequestedBy)
            .WithMany()
            .HasForeignKey(r => r.RequestedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(r => r.SubmittedBy)
            .WithMany()
            .HasForeignKey(r => r.SubmittedById)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => r.Number).IsUnique();

        // The approvals queue (F12): waiting requisitions, oldest first.
        builder.HasIndex(r => new { r.Status, r.SubmittedAt });
        builder.HasIndex(r => r.RequestedById);
    }
}

public class RequisitionLineConfiguration : IEntityTypeConfiguration<RequisitionLine>
{
    public void Configure(EntityTypeBuilder<RequisitionLine> builder)
    {
        builder.ToTable("RequisitionLines", t =>
        {
            t.HasCheckConstraint("CK_RequisitionLines_QuantityRequested", "\"QuantityRequested\" >= 1");
        });
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Note).HasMaxLength(RequisitionLine.NoteMaxLength);

        // Only drafts are deletable, and a draft's lines are referenced by nothing else.
        builder.HasOne(l => l.Requisition)
            .WithMany(r => r.Lines)
            .HasForeignKey(l => l.RequisitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(l => l.Product)
            .WithMany()
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // One line per part per requisition; also serves "lines of this requisition".
        builder.HasIndex(l => new { l.RequisitionId, l.ProductId }).IsUnique();

        builder.HasIndex(l => l.ProductId);
    }
}
