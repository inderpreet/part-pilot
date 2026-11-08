using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Services;

public class BusinessSettingsTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private ServiceProvider _provider = null!;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        var services = new ServiceCollection();
        services.AddScoped(_ => _db.CreateContext());
        _provider = services.BuildServiceProvider();
    }

    public async Task DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _db.DisposeAsync();
    }

    private SettingsService CreateService() => new(_provider.GetRequiredService<IServiceScopeFactory>());

    private static BusinessSettings Valid() => new("Acme Ltd", "eur", "€", true, "ORD-");

    [Fact]
    public async Task Get_returns_the_seeded_defaults()
    {
        var settings = await CreateService().GetBusinessSettingsAsync();

        Assert.Equal(new BusinessSettings("My Company", "USD", "$", false, "PO-"), settings);
    }

    [Fact]
    public async Task Update_saves_everything_normalises_the_currency_code_and_refreshes_the_cache()
    {
        var service = CreateService();
        await service.GetBusinessSettingsAsync(); // prime cache

        var result = await service.UpdateBusinessSettingsAsync(Valid() with { CompanyName = "  Acme Ltd  " });

        Assert.True(result.Succeeded);
        var expected = new BusinessSettings("Acme Ltd", "EUR", "€", true, "ORD-");
        Assert.Equal(expected, result.Value);
        Assert.Equal(expected, await service.GetBusinessSettingsAsync());
        Assert.Equal(expected, await CreateService().GetBusinessSettingsAsync()); // from the database
    }

    [Fact]
    public async Task Update_works_when_a_setting_row_is_missing()
    {
        await using (var context = _db.CreateContext())
        {
            context.AppSettings.RemoveRange(context.AppSettings);
            await context.SaveChangesAsync();
        }

        Assert.True((await CreateService().UpdateBusinessSettingsAsync(Valid())).Succeeded);
        Assert.Equal("Acme Ltd", (await CreateService().GetBusinessSettingsAsync()).CompanyName);
    }

    [Fact]
    public async Task An_empty_po_prefix_is_allowed()
    {
        var result = await CreateService().UpdateBusinessSettingsAsync(Valid() with { PurchaseOrderNumberPrefix = "" });

        Assert.True(result.Succeeded);
        Assert.Equal("", (await CreateService().GetBusinessSettingsAsync()).PurchaseOrderNumberPrefix);
    }

    [Theory]
    [InlineData("US")]
    [InlineData("USDX")]
    [InlineData("U$D")]
    [InlineData("12A")]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Currency_code_must_be_exactly_three_letters(string code)
    {
        var result = await CreateService().UpdateBusinessSettingsAsync(Valid() with { CurrencyCode = code });

        var error = Assert.Single(result.Errors);
        Assert.Equal(BusinessSettings.Fields.CurrencyCode, error.Field);
    }

    [Theory]
    [InlineData("", BusinessSettings.Fields.CompanyName)]
    [InlineData("   ", BusinessSettings.Fields.CompanyName)]
    public async Task Company_name_is_required(string name, string field)
    {
        var result = await CreateService().UpdateBusinessSettingsAsync(Valid() with { CompanyName = name });

        Assert.Equal(field, Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task Field_lengths_are_limited()
    {
        var result = await CreateService().UpdateBusinessSettingsAsync(Valid() with
        {
            CompanyName = new string('x', BusinessSettings.CompanyNameMaxLength + 1),
            CurrencySymbol = new string('x', BusinessSettings.CurrencySymbolMaxLength + 1),
            PurchaseOrderNumberPrefix = new string('x', BusinessSettings.PurchaseOrderPrefixMaxLength + 1),
        });

        Assert.Equal(
            [BusinessSettings.Fields.CompanyName, BusinessSettings.Fields.CurrencySymbol, BusinessSettings.Fields.PurchaseOrderNumberPrefix],
            result.Errors.Select(e => e.Field));
    }

    [Fact]
    public async Task Prefix_of_exactly_ten_characters_is_accepted()
    {
        var result = await CreateService().UpdateBusinessSettingsAsync(Valid() with { PurchaseOrderNumberPrefix = "0123456789" });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Prefix_cannot_contain_spaces()
    {
        var result = await CreateService().UpdateBusinessSettingsAsync(Valid() with { PurchaseOrderNumberPrefix = "P O-" });

        Assert.Equal(BusinessSettings.Fields.PurchaseOrderNumberPrefix, Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task Symbol_is_required()
    {
        var result = await CreateService().UpdateBusinessSettingsAsync(Valid() with { CurrencySymbol = " " });

        Assert.Equal(BusinessSettings.Fields.CurrencySymbol, Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task A_rejected_update_changes_nothing()
    {
        var service = CreateService();

        var result = await service.UpdateBusinessSettingsAsync(Valid() with { CurrencyCode = "nope" });

        Assert.False(result.Succeeded);
        Assert.Equal(new BusinessSettings("My Company", "USD", "$", false, "PO-"), await CreateService().GetBusinessSettingsAsync());
        Assert.Equal("My Company", await service.GetStringAsync(SettingKeys.CompanyName));
    }

    [Fact]
    public async Task The_project_number_prefix_defaults_to_prj_and_can_be_changed_or_emptied()
    {
        Assert.Equal("PRJ-", (await CreateService().GetBusinessSettingsAsync()).ProjectNumberPrefix);

        Assert.True((await CreateService().UpdateBusinessSettingsAsync(Valid() with { ProjectNumberPrefix = " JOB/ " })).Succeeded);
        Assert.Equal("JOB/", (await CreateService().GetBusinessSettingsAsync()).ProjectNumberPrefix);
        Assert.Equal("JOB/", await CreateService().GetStringAsync(SettingKeys.ProjectNumberPrefix));

        Assert.True((await CreateService().UpdateBusinessSettingsAsync(Valid() with { ProjectNumberPrefix = "" })).Succeeded);
        Assert.Equal("", (await CreateService().GetBusinessSettingsAsync()).ProjectNumberPrefix);
    }

    [Fact]
    public async Task A_project_prefix_that_is_too_long_or_has_spaces_is_refused_on_its_own_field()
    {
        var tooLong = await CreateService().UpdateBusinessSettingsAsync(Valid() with { ProjectNumberPrefix = new string('x', BusinessSettings.ProjectPrefixMaxLength + 1) });
        var spaced = await CreateService().UpdateBusinessSettingsAsync(Valid() with { ProjectNumberPrefix = "P R-" });

        Assert.Equal(BusinessSettings.Fields.ProjectNumberPrefix, Assert.Single(tooLong.Errors).Field);
        Assert.Equal(BusinessSettings.Fields.ProjectNumberPrefix, Assert.Single(spaced.Errors).Field);
        Assert.Equal("PRJ-", (await CreateService().GetBusinessSettingsAsync()).ProjectNumberPrefix);
    }
}