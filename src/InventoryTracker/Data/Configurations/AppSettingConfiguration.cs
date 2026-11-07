using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InventoryTracker.Data.Configurations;

public class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("AppSettings");
        builder.HasKey(s => s.Key);
        builder.Property(s => s.Key).HasMaxLength(100).UseCollation("NOCASE");
        builder.Property(s => s.Value).IsRequired();

        builder.HasData(SettingKeys.Defaults.Select(d => new AppSetting { Key = d.Key, Value = d.Value }));
    }
}
