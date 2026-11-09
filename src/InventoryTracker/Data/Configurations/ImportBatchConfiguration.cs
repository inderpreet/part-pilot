using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class ImportBatchConfiguration : IEntityTypeConfiguration<ImportBatch>
{
    public void Configure(EntityTypeBuilder<ImportBatch> builder)
    {
        var kinds = string.Join(", ", Enum.GetNames<ImportKind>().Select(n => $"'{n}'"));

        builder.ToTable("ImportBatches", t =>
        {
            t.HasCheckConstraint("CK_ImportBatches_Kind", $"\"Kind\" IN ({kinds})");
            t.HasCheckConstraint("CK_ImportBatches_Counts", "\"RowCount\" >= 0 AND \"CreatedCount\" >= 0 AND \"UpdatedCount\" >= 0 AND \"SkippedCount\" >= 0");
        });

        builder.HasKey(b => b.Id);

        builder.Property(b => b.Kind).IsRequired().HasConversion<string>().HasMaxLength(20);
        builder.Property(b => b.FileName).IsRequired().HasMaxLength(ImportBatch.FileNameMaxLength);
        builder.Property(b => b.Options).HasMaxLength(ImportBatch.OptionsMaxLength);

        builder.HasOne(b => b.CreatedBy)
            .WithMany()
            .HasForeignKey(b => b.CreatedById)
            .OnDelete(DeleteBehavior.Restrict);

        // The import history lists the newest first.
        builder.HasIndex(b => b.CreatedAt);
        builder.HasIndex(b => b.CreatedById);

        // Stock movements an import posted. Restrict: a batch with movements can never be removed.
        builder.HasMany<StockTransaction>()
            .WithOne(t => t.ImportBatch)
            .HasForeignKey(t => t.ImportBatchId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
