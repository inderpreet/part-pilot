namespace InventoryTracker.Data.Entities;

/// <summary>Business setting stored as a key/value pair (see <see cref="SettingKeys"/>).</summary>
public class AppSetting
{
    public required string Key { get; set; }

    public string Value { get; set; } = string.Empty;
}
