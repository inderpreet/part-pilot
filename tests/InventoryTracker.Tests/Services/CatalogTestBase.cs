using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Data.Interceptors;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InventoryTracker.Tests.Services;

/// <summary>Real EF + SQLite in-memory; each test gets its own database and can open as many "requests" (contexts) as it likes.</summary>
public abstract class CatalogTestBase : IAsyncLifetime
{
    protected SqliteTestDb Db { get; private set; } = null!;

    public async Task InitializeAsync() => Db = await SqliteTestDb.CreateAsync();

    public async Task DisposeAsync() => await Db.DisposeAsync();

    /// <summary>A new context + service pair, like one web request. Dispose the context when done.</summary>
    protected (AppDbContext Context, ProductService Products, CategoryService Categories) NewRequest(params IInterceptor[] extra)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(Db.Connection)
            .AddInterceptors([new TimestampInterceptor(), new RowVersionInterceptor(), .. extra])
            .Options;
        var context = new AppDbContext(options);
        return (context, new ProductService(context), new CategoryService(context));
    }

    protected static ProductInput Input(
        string sku = "SKU-1",
        string name = "Widget",
        string? barcode = null,
        int? categoryId = null,
        string? description = null,
        string? unit = null,
        string? bin = null,
        int reorderPoint = 0,
        int reorderQuantity = 0,
        long costCents = 0) =>
        new(sku, name, description, categoryId, unit, barcode, bin, reorderPoint, reorderQuantity, costCents);

    protected async Task<ProductDetail> CreateProductAsync(ProductInput input)
    {
        var (context, products, _) = NewRequest();
        await using (context)
        {
            var result = await products.CreateAsync(input);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            return result.Value;
        }
    }

    protected async Task<CategoryListItem> CreateCategoryAsync(string name)
    {
        var (context, _, categories) = NewRequest();
        await using (context)
        {
            var result = await categories.CreateAsync(new CategoryInput(name, null));
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            return result.Value;
        }
    }

    /// <summary>Stands in for F03 until the ledger exists: sets the cached quantity directly.</summary>
    protected async Task SetOnHandAsync(int productId, int quantity)
    {
        var (context, _, _) = NewRequest();
        await using (context)
        {
            await context.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.QuantityOnHand, quantity));
        }
    }

    protected async Task ArchiveAsync(int productId)
    {
        var (context, products, _) = NewRequest();
        await using (context)
        {
            Assert.True((await products.SetActiveAsync(productId, false)).Succeeded);
        }
    }

    /// <summary>Runs <paramref name="sql"/> on the shared connection immediately before the next SaveChanges - simulates a concurrent request winning a race.</summary>
    protected sealed class RunSqlBeforeSave(string sql) : SaveChangesInterceptor
    {
        private bool _done;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_done)
            {
                _done = true;
                await eventData.Context!.Database.ExecuteSqlRawAsync(sql, cancellationToken);
            }

            return result;
        }
    }
}
