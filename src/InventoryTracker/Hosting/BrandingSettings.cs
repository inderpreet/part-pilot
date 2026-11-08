using InventoryTracker.Data;

namespace InventoryTracker.Hosting;

/// <summary>
/// What the app calls itself and the default company name, read once from configuration
/// (<c>Branding:ProductName</c>, <c>Branding:CompanyName</c>; as environment variables or <c>.env</c> lines
/// <c>Branding__ProductName</c> and <c>Branding__CompanyName</c>).
/// <para>
/// <see cref="ProductName"/> is the product's identity and is shown in the browser tab title, footer, first-run
/// setup page and startup log. <see cref="CompanyName"/> is only the <b>default</b> for the business name: a
/// company name saved in Admin → Settings always wins (see <c>SettingsService</c>).
/// </para>
/// </summary>
public sealed record BrandingSettings(string ProductName, string CompanyName)
{
    public const string ProductNameKey = "Branding:ProductName";
    public const string CompanyNameKey = "Branding:CompanyName";

    public const string DefaultProductName = "Part Pilot";

    public const int MaxLength = 100;

    /// <summary>The built-in defaults, used when nothing is configured.</summary>
    public static BrandingSettings Default { get; } = new(DefaultProductName, SettingKeys.Defaults[SettingKeys.CompanyName]);

    public static BrandingSettings Resolve(IConfiguration config)
        => new(
            Read(config, ProductNameKey, DefaultProductName),
            Read(config, CompanyNameKey, Default.CompanyName));

    private static string Read(IConfiguration config, string key, string fallback)
    {
        var value = config[key]?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return fallback;
        }

        if (value.Length > MaxLength)
        {
            throw new InvalidOperationException($"{key} must be at most {MaxLength} characters (got {value.Length}).");
        }

        if (value.Any(char.IsControl))
        {
            throw new InvalidOperationException($"{key} cannot contain control characters.");
        }

        return value;
    }
}
