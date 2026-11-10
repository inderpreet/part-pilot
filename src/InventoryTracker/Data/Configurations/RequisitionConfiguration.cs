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

public class RequisitionLineDecisionConfiguration : IEntityTypeConfiguration<RequisitionLineDecision>
{
    public void Configure(EntityTypeBuilder<RequisitionLineDecision> builder)
    {
        var decisions = string.Join(", ", Enum.GetNames<RequisitionDecision>().Select(n => $"'{n}'"));

        builder.ToTable("RequisitionLineDecisions", t =>
        {
            t.HasCheckConstraint("CK_RequisitionLineDecisions_Decision", $"\"Decision\" IN ({decisions})");
            t.HasCheckConstraint(
                "CK_RequisitionLineDecisions_Quantity",
                "(\"Decision\" = 'Approved' AND \"QuantityApproved\" >= 1) OR (\"Decision\" = 'Rejected' AND \"QuantityApproved\" = 0)");
        });
        builder.HasKey(d => d.Id);

        builder.Property(d => d.Decision).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Reason).HasMaxLength(RequisitionLineDecision.ReasonMaxLength);

        // One decision per line (the foreign key's unique index), which also stops two people deciding the same line.
        builder.HasOne(d => d.RequisitionLine)
            .WithOne(l => l.Decision)
            .HasForeignKey<RequisitionLineDecision>(d => d.RequisitionLineId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(d => d.DecidedBy)
            .WithMany()
            .HasForeignKey(d => d.DecidedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RequisitionLineOrderConfiguration : IEntityTypeConfiguration<RequisitionLineOrder>
{
    public void Configure(EntityTypeBuilder<RequisitionLineOrder> builder)
    {
        builder.ToTable("RequisitionLineOrders", t =>
        {
            t.HasCheckConstraint("CK_RequisitionLineOrders_QuantityOrdered", "\"QuantityOrdered\" >= 1");
        });
        builder.HasKey(o => o.Id);

        // One link per requisition line (unique), which also stops two people ordering it at once.
        builder.HasOne(o => o.RequisitionLine)
            .WithOne(l => l.Order)
            .HasForeignKey<RequisitionLineOrder>(o => o.RequisitionLineId)
            .OnDelete(DeleteBehavior.Cascade);

        // Removing the PO line, or deleting its draft, releases the requisition line (D25).
        builder.HasOne(o => o.PurchaseOrderLine)
            .WithMany()
            .HasForeignKey(o => o.PurchaseOrderLineId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(o => o.ConvertedBy)
            .WithMany()
            .HasForeignKey(o => o.ConvertedById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class RequisitionCancellationConfiguration : IEntityTypeConfiguration<RequisitionCancellation>
{
    public void Configure(EntityTypeBuilder<RequisitionCancellation> builder)
    {
        builder.ToTable("RequisitionCancellations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Reason).IsRequired().HasMaxLength(RequisitionCancellation.ReasonMaxLength);

        builder.HasOne(c => c.Requisition)
            .WithOne(r => r.Cancellation)
            .HasForeignKey<RequisitionCancellation>(c => c.RequisitionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(c => c.CancelledBy)
            .WithMany()
            .HasForeignKey(c => c.CancelledById)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
