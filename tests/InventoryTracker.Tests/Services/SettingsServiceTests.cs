using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace InventoryTracker.Tests.Services;

public class SettingsServiceTests : IAsyncLifetime
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

    [Fact]
    public async Task Returns_seeded_defaults()
    {
        var settings = CreateService();

        Assert.Equal("My Company", await settings.GetStringAsync(SettingKeys.CompanyName));
        Assert.False(await settings.GetBoolAsync(SettingKeys.StockAllowNegative));
        Assert.Equal("PO-", await settings.GetStringAsync(SettingKeys.PurchaseOrderNumberPrefix));
    }

    [Fact]
    public async Task Falls_back_to_default_when_a_row_is_missing()
    {
        await using (var context = _db.CreateContext())
        {
            await context.AppSettings.ExecuteDeleteAsync();
        }

        Assert.Equal("USD", await CreateService().GetStringAsync(SettingKeys.CurrencyCode));
    }

    [Fact]
    public async Task Set_persists_and_invalidates_the_cache()
    {
        var settings = CreateService();
        Assert.Equal("My Company", await settings.GetStringAsync(SettingKeys.CompanyName)); // primes the cache

        var result = await settings.SetAsync(SettingKeys.CompanyName, "Acme");

        Assert.True(result.Succeeded);
        Assert.Equal("Acme", await settings.GetStringAsync(SettingKeys.CompanyName));
        Assert.Equal("Acme", await CreateService().GetStringAsync(SettingKeys.CompanyName)); // read back from the database
    }

    [Fact]
    public async Task Set_inserts_a_row_when_none_exists()
    {
        await using (var context = _db.CreateContext())
        {
            await context.AppSettings.ExecuteDeleteAsync();
        }

        var settings = CreateService();
        Assert.True((await settings.SetAsync(SettingKeys.StockAllowNegative, true)).Succeeded);

        Assert.True(await settings.GetBoolAsync(SettingKeys.StockAllowNegative));
        await using var verify = _db.CreateContext();
        Assert.Equal("true", (await verify.AppSettings.SingleAsync()).Value);
    }

    [Fact]
    public async Task Typed_setters_round_trip()
    {
        var settings = CreateService();

        await settings.SetAsync(SettingKeys.StockAllowNegative, true);
        Assert.True(await settings.GetBoolAsync(SettingKeys.StockAllowNegative));

        await settings.SetAsync(SettingKeys.StockAllowNegative, false);
        Assert.False(await settings.GetBoolAsync(SettingKeys.StockAllowNegative));
    }

    [Fact]
    public async Task Unknown_keys_are_rejected()
    {
        var settings = CreateService();

        var result = await settings.SetAsync("Not.AKey", "x");

        Assert.False(result.Succeeded);
        Assert.Equal("setting.unknown", result.Errors[0].Code);
        await Assert.ThrowsAsync<ArgumentException>(() => settings.GetStringAsync("Not.AKey"));
    }

    [Fact]
    public async Task Unparseable_stored_values_fall_back_to_defaults()
    {
        var settings = CreateService();
        await settings.SetAsync(SettingKeys.StockAllowNegative, "garbage");

        Assert.False(await settings.GetBoolAsync(SettingKeys.StockAllowNegative));
    }
}
