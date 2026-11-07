using InventoryTracker.Data.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.Property(u => u.DisplayName).IsRequired().HasMaxLength(ApplicationUser.DisplayNameMaxLength);
        builder.Property(u => u.IsActive).IsRequired();
        // LastSignInAt: nullable UTC DateTime, converted by the model-wide UTC convention.
    }
}

/// <summary>
/// Seeds the three roles. IDs and concurrency stamps are fixed so the seed is identical on every machine and a
/// later migration never sees a spurious change.
/// </summary>
public class RoleConfiguration : IEntityTypeConfiguration<IdentityRole>
{
    public const string AdminId = "a1b2c3d4-0001-4000-8000-000000000001";
    public const string StaffId = "a1b2c3d4-0002-4000-8000-000000000002";
    public const string ViewerId = "a1b2c3d4-0003-4000-8000-000000000003";

    public void Configure(EntityTypeBuilder<IdentityRole> builder)
    {
        builder.HasData(
            Seed(AdminId, Roles.Admin),
            Seed(StaffId, Roles.Staff),
            Seed(ViewerId, Roles.Viewer));
    }

    private static IdentityRole Seed(string id, string name) => new(name)
    {
        Id = id,
        NormalizedName = name.ToUpperInvariant(),
        ConcurrencyStamp = id, // any constant string; fixed so the model snapshot is stable
    };
}
