using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class ProjectConfiguration : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> builder)
    {
        builder.ToTable("Projects", t =>
        {
            t.HasCheckConstraint("CK_Projects_TargetUnits", "\"TargetUnits\" >= 1");
        });
        builder.HasKey(p => p.Id);

        // NOCASE folds ASCII only (same as every other name/number column); NOCASE also makes ORDER BY case-insensitive.
        builder.Property(p => p.Name).IsRequired().HasMaxLength(Project.NameMaxLength).UseCollation("NOCASE");
        builder.Property(p => p.Number).IsRequired().HasMaxLength(Project.NumberMaxLength).UseCollation("NOCASE");
        builder.Property(p => p.Description).HasMaxLength(Project.DescriptionMaxLength);
        builder.Property(p => p.Notes).HasMaxLength(Project.NotesMaxLength);
        builder.Property(p => p.TargetUnits).HasDefaultValue(1).ValueGeneratedNever();
        builder.Property(p => p.IsActive).IsRequired();

        builder.HasIndex(p => p.Name).IsUnique();
        builder.HasIndex(p => p.Number).IsUnique();
        builder.HasIndex(p => new { p.IsActive, p.Name });
    }
}

public class BomLineConfiguration : IEntityTypeConfiguration<BomLine>
{
    public void Configure(EntityTypeBuilder<BomLine> builder)
    {
        builder.ToTable("BomLines", t =>
        {
            t.HasCheckConstraint("CK_BomLines_QuantityPerUnit", "\"QuantityPerUnit\" >= 1");
        });
        builder.HasKey(l => l.Id);

        builder.Property(l => l.Note).HasMaxLength(BomLine.NoteMaxLength);

        // A project's lines go with it; only a project without builds can be deleted.
        builder.HasOne(l => l.Project)
            .WithMany(p => p.Lines)
            .HasForeignKey(l => l.ProjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // Products are archived, never deleted; a BOM must never lose a part.
        builder.HasOne(l => l.Product)
            .WithMany()
            .HasForeignKey(l => l.ProductId)
            .OnDelete(DeleteBehavior.Restrict);

        // One line per product per project; also serves "lines of this project".
        builder.HasIndex(l => new { l.ProjectId, l.ProductId }).IsUnique();

        // "Used in projects" on the product page.
        builder.HasIndex(l => l.ProductId);
    }
}

public class ProjectBuildConfiguration : IEntityTypeConfiguration<ProjectBuild>
{
    public void Configure(EntityTypeBuilder<ProjectBuild> builder)
    {
        builder.ToTable("ProjectBuilds", t =>
        {
            t.HasCheckConstraint("CK_ProjectBuilds_Units", "\"Units\" >= 1");
        });
        builder.HasKey(b => b.Id);

        builder.Property(b => b.Note).HasMaxLength(ProjectBuild.NoteMaxLength);

        // A project that has been built is archived, never deleted.
        builder.HasOne(b => b.Project)
            .WithMany(p => p.Builds)
            .HasForeignKey(b => b.ProjectId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(b => b.User)
            .WithMany()
            .HasForeignKey(b => b.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(b => new { b.ProjectId, b.Id });
        builder.HasIndex(b => b.UserId);
    }
}
