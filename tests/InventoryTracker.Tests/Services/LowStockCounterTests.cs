using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Data.Interceptors;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-06-2: the cached nav badge count, and the change signal that refreshes it early.</summary>
public sealed class LowStockCounterTests : ReportingTestBase
{
    // Every test gets its own signal, so parallel test classes cannot disturb what is asserted here.
    private readonly ReportingChangeSignal _signal = new();

    /// <summary>A scope factory whose contexts share the test database and carry the change interceptor, like the real app.</summary>
    private ServiceProvider Services()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Db.Connection)
            .AddInterceptors(new TimestampInterceptor(), new RowVersionInterceptor(), new ReportingChangeInterceptor(_signal))
            .Options));
        return services.BuildServiceProvider();
    }

    private LowStockCounter CounterFor(ServiceProvider provider)
        => new(provider.GetRequiredService<IServiceScopeFactory>(), Clock, _signal);

    private async Task<int> NewLowPartAsync(string sku)
    {
        var supplier = await NewSupplierAsync("S-" + sku);
        return await LowPartAsync(sku, supplier, onHand: 0, reorderPoint: 5);
    }

    [Fact]
    public async Task The_count_is_the_number_of_low_parts_and_invalidate_forces_a_recount()
    {
        await using var provider = Services();
        var counter = CounterFor(provider);

        Assert.Equal(0, await counter.GetAsync());

        await NewLowPartAsync("A");
        Assert.Equal(0, await counter.GetAsync());
        counter.Invalidate();
        Assert.Equal(1, await counter.GetAsync());
    }

    [Fact]
    public async Task The_count_is_cached_for_a_minute_unless_something_changes()
    {
        await using var provider = Services();
        var counter = CounterFor(provider);
        Assert.Equal(0, await counter.GetAsync());

        // A change written outside the app's contexts (no signal): the cached value stands...
        await using (var context = Db.CreateContext())
        {
            context.Products.Add(new Product { Sku = "RAW", Name = "Raw", ReorderPoint = 5 });
            await context.SaveChangesAsync();
        }

        Clock.UtcNow = Clock.UtcNow.AddSeconds(LowStockCounter.CacheSeconds - 1);
        Assert.Equal(0, await counter.GetAsync());

        // ...until it is a minute old.
        Clock.UtcNow = Clock.UtcNow.AddSeconds(2);
        Assert.Equal(1, await counter.GetAsync());
    }

    [Fact]
    public async Task Saving_a_stock_movement_through_the_app_refreshes_the_count_at_once()
    {
        var supplier = await NewSupplierAsync();
        var part = await LowPartAsync("P", supplier, onHand: 10, reorderPoint: 5);
        await using var provider = Services();
        var counter = CounterFor(provider);
        Assert.Equal(0, await counter.GetAsync());

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var stock = new StockService(context, Settings, Clock, NullLogger<StockService>.Instance);
            Assert.True((await stock.PostAsync([Issue(part, 8)], Who)).Succeeded);
        }

        Assert.Equal(1, await counter.GetAsync());
    }

    [Fact]
    public async Task Saving_a_part_or_an_order_signals_a_change()
    {
        await using var provider = Services();
        var before = _signal.Version;

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Products.Add(new Product { Sku = "NEW", Name = "New" });
            await context.SaveChangesAsync();
        }

        Assert.Equal(before + 1, _signal.Version);
    }

    [Fact]
    public async Task Changes_that_do_not_touch_stock_or_orders_leave_the_cache_alone()
    {
        await using var provider = Services();
        var before = _signal.Version;

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Categories.Add(new Category { Name = "Unrelated" });
            await context.SaveChangesAsync();
        }

        Assert.Equal(before, _signal.Version);
    }

    [Fact]
    public async Task A_failed_save_does_not_signal_a_change()
    {
        await using var provider = Services();
        await NewLowPartAsync("DUP");
        var before = _signal.Version;

        await using (var scope = provider.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            context.Products.Add(new Product { Sku = "DUP", Name = "Duplicate SKU" });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        }

        Assert.Equal(before, _signal.Version);
    }
}
