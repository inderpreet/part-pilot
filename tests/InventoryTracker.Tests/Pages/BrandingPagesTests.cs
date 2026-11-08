using System.Net;
using System.Text.RegularExpressions;
using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Pages;

/// <summary>Product name and default company name come from configuration (<c>Branding:*</c>), not from code.</summary>
public sealed class BrandingPagesTests : IDisposable
{
    private static readonly Dictionary<string, string?> Acme = new()
    {
        ["Branding:ProductName"] = "Acme Pilot",
        ["Branding:CompanyName"] = "Acme Tools",
    };

    private readonly AppFactory _factory = new() { Configuration = Acme };
    private readonly AppFactory _firstRun = new() { Configuration = Acme, SeedUsers = false, DefaultTestRole = null };

    public void Dispose()
    {
        _factory.Dispose();
        _firstRun.Dispose();
    }

    [Fact]
    public async Task Signed_in_pages_use_the_configured_product_and_company_names()
    {
        using var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/");

        // Tab title = product; rail brand = company (the placeholder row is replaced by the configured default).
        Assert.Matches("<title>Dashboard (·|&#xB7;) Acme Pilot</title>", html);
        Assert.Contains("class=\"brand-name\">Acme Tools</strong>", html);
        Assert.Matches("<small>Acme Tools (·|&#xB7;) Acme Pilot v", html);
        Assert.DoesNotContain("Inventory Tracker", html);
        Assert.DoesNotContain("My Company", html);
    }

    [Fact]
    public async Task A_company_saved_in_admin_settings_wins_over_the_configured_default()
    {
        var settings = _factory.Services.GetRequiredService<ISettingsService>();
        await settings.SetAsync(SettingKeys.CompanyName, "Saved Co");
        try
        {
            using var client = _factory.CreateClient();
            var html = await client.GetStringAsync("/");

            Assert.Contains("class=\"brand-name\">Saved Co</strong>", html);
            Assert.DoesNotContain("Acme Tools", html);
            Assert.Matches("<title>Dashboard (·|&#xB7;) Acme Pilot</title>", html); // product name is unaffected
        }
        finally
        {
            await settings.SetAsync(SettingKeys.CompanyName, SettingKeys.Defaults[SettingKeys.CompanyName]);
        }
    }

    [Fact]
    public async Task The_settings_page_offers_the_configured_company_until_one_is_saved()
    {
        using var client = _factory.CreateClient();

        var html = await client.GetStringAsync("/Admin/Settings");

        Assert.Matches("name=\"Input.CompanyName\" value=\"Acme Tools\"", html);
    }

    [Fact]
    public async Task First_run_setup_uses_the_product_name_and_prefills_the_company()
    {
        using var client = _firstRun.CreateAnonymousClient();

        var response = await client.GetAsync("/Account/Setup");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<title>Set up Acme Pilot</title>", html); // the product name is not repeated when the page title has it
        Assert.Matches("<input[^>]*name=\"Input.CompanyName\"[^>]*value=\"Acme Tools\"", html);
        Assert.Matches("<strong>Acme Tools</strong>", html); // plain header
        Assert.DoesNotContain("Inventory Tracker", html);
    }

    [Fact]
    public async Task The_sign_in_page_shows_the_product_name_in_the_footer()
    {
        using var client = _factory.CreateAnonymousClient();

        var html = await client.GetStringAsync("/Account/Login");

        Assert.Matches("<small>Acme Tools (·|&#xB7;) Acme Pilot v", html);
        Assert.Matches("<title>Sign in (·|&#xB7;) Acme Pilot</title>", html);
    }

    [Fact]
    public async Task Names_are_html_encoded()
    {
        using var factory = new AppFactory { Configuration = new Dictionary<string, string?> { ["Branding:ProductName"] = "<b>Pilot</b>" } };
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("&lt;b&gt;Pilot&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Pilot</b>", html);
    }

    [Fact]
    public async Task Without_configuration_the_product_is_part_pilot()
    {
        using var factory = new AppFactory();
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Matches(new Regex("<title>Dashboard (·|&#xB7;) Part Pilot</title>"), html);
        Assert.Contains("class=\"brand-name\">My Company</strong>", html);
    }
}
