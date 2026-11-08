using InventoryTracker.Hosting;
using Microsoft.Extensions.Configuration;

namespace InventoryTracker.Tests.Hosting;

public class BrandingSettingsTests
{
    private static BrandingSettings Resolve(params (string Key, string? Value)[] values)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(values.ToDictionary(v => v.Key, v => v.Value))
            .Build();
        return BrandingSettings.Resolve(config);
    }

    [Fact]
    public void Defaults_to_part_pilot_and_the_seeded_company_placeholder()
    {
        var branding = Resolve();

        Assert.Equal("Part Pilot", branding.ProductName);
        Assert.Equal("My Company", branding.CompanyName);
        Assert.Equal(branding, BrandingSettings.Default);
    }

    [Fact]
    public void Reads_both_values_from_configuration()
    {
        var branding = Resolve((BrandingSettings.ProductNameKey, "Acme Pilot"), (BrandingSettings.CompanyNameKey, "Acme Tools"));

        Assert.Equal(new BrandingSettings("Acme Pilot", "Acme Tools"), branding);
    }

    [Fact]
    public void Env_file_lines_with_spaces_in_the_value_are_read_whole()
    {
        // A .env line becomes an environment variable; Branding__X is the same key as Branding:X in configuration.
        var parsed = DotEnv.Parse(["Branding__ProductName=Part Pilot", "Branding__CompanyName=Anhad Tech  # my company"]).ToDictionary(p => p.Key, p => p.Value);

        Assert.Equal("Part Pilot", parsed["Branding__ProductName"]);
        Assert.Equal("Anhad Tech", parsed["Branding__CompanyName"]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_values_fall_back_to_the_defaults(string? blank)
    {
        var branding = Resolve((BrandingSettings.ProductNameKey, blank), (BrandingSettings.CompanyNameKey, blank));

        Assert.Equal(BrandingSettings.Default, branding);
    }

    [Fact]
    public void Values_are_trimmed()
    {
        var branding = Resolve((BrandingSettings.ProductNameKey, "  Acme Pilot  "), (BrandingSettings.CompanyNameKey, "\tAcme Tools "));

        Assert.Equal(new BrandingSettings("Acme Pilot", "Acme Tools"), branding);
    }

    [Theory]
    [InlineData(BrandingSettings.ProductNameKey)]
    [InlineData(BrandingSettings.CompanyNameKey)]
    public void A_value_longer_than_the_limit_is_rejected_with_the_key_in_the_message(string key)
    {
        var tooLong = new string('x', BrandingSettings.MaxLength + 1);

        var error = Assert.Throws<InvalidOperationException>(() => Resolve((key, tooLong)));

        Assert.Contains(key, error.Message);
    }

    [Fact]
    public void A_value_at_the_limit_is_accepted()
    {
        var atLimit = new string('x', BrandingSettings.MaxLength);

        Assert.Equal(atLimit, Resolve((BrandingSettings.ProductNameKey, atLimit)).ProductName);
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Resolve((BrandingSettings.ProductNameKey, "Part\nPilot")));

        Assert.Contains(BrandingSettings.ProductNameKey, error.Message);
    }
}
