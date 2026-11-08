using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class StockTransactionConfiguration : IEntityTypeConfiguration<StockTransaction>
{
    /// <summary>Name of the trigger that blocks UPDATE; created by the AddStockLedger migration.</summary>
    public const string NoUpdateTrigger = "TR_StockTransactions_NoUpdate";

    /// <summary>Name of the trigger that blocks DELETE; created by the AddStockLedger migration.</summary>
    public const string NoDeleteTrigger = "TR_StockTransactions_NoDelete";

    /// <summary>Message of the error raised by the immutability triggers.</summary>
    public const string ImmutableMessage = "Stock transactions are immutable - post a Reversal instead.";

    public void Configure(EntityTypeBuilder<StockTransaction> builder)
    {
        var types = string.Join(", ", Enum.GetNames<StockTransactionType>().Select(n => $"'{n}'"));

        builder.ToTable("StockTransactions", t =>
        {
            t.HasCheckConstraint("CK_StockTransactions_QuantityDelta", "\"QuantityDelta\" <> 0");
            t.HasCheckConstraint("CK_StockTransactions_Type", $"\"Type\" IN ({types})");

            // A Reversal always points at the row it negates, and nothing else does.
            t.HasCheckConstraint(
                "CK_StockTransactions_Reversal",
                "(\"Type\" = 'Reversal') = (\"ReversesTransactionId\" IS NOT NULL)");
        });

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Type).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Reason).HasMaxLength(StockTransaction.ReasonMaxLength);
        builder.Property(t => t.Reference).HasMaxLength(StockTransaction.ReferenceMaxLength);
        builder.Property(t => t.Note).HasMaxLength(StockTransaction.NoteMaxLength);

        builder.HasOne(t => t.Product)
            .WithMany(p => p.StockTransactions)
            .HasForeignKey(t => t.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(t => t.ReversesTransaction)
            .WithMany()
            .HasForeignKey(t => t.ReversesTransactionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => new { t.ProductId, t.Id });
        builder.HasIndex(t => t.OccurredAt);
        builder.HasIndex(t => t.Type);
        builder.HasIndex(t => t.UserId);
        // A line that has receipts cannot be deleted (only drafts, which have none, are).
        builder.HasOne(t => t.PurchaseOrderLine)
            .WithMany(l => l.Receipts)
            .HasForeignKey(t => t.PurchaseOrderLineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(t => t.PurchaseOrderLineId);

        // A transaction can be reversed only once.
        builder.HasIndex(t => t.ReversesTransactionId).IsUnique().HasFilter("\"ReversesTransactionId\" IS NOT NULL");
    }
}
