using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;

namespace InventoryTracker.Tests.Services;

/// <summary>Purchase order test database plus helpers for the F06 services (each call is its own "request").</summary>
public abstract class ReportingTestBase : PurchaseOrderTestBase
{
    protected async Task<TResult> WithReportsAsync<TResult>(Func<ReportService, Task<TResult>> action)
    {
        await using var context = Db.CreateContext();
        return await action(new ReportService(context, Clock));
    }

    protected async Task<TResult> WithDashboardAsync<TResult>(Func<DashboardService, Task<TResult>> action)
    {
        await using var context = Db.CreateContext();
        return await action(new DashboardService(context, Clock));
    }

    protected async Task<TResult> WithReorderAsync<TResult>(Func<ReorderService, Task<TResult>> action)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            return await action(new ReorderService(context, orders));
        }
    }

    /// <summary>A part with a preferred supplier link.</summary>
    protected async Task<int> LowPartAsync(
        string sku, int supplierId, int onHand = 0, int reorderPoint = 10, int reorderQuantity = 0, long costCents = 100, int minOrder = 1, int? categoryId = null)
    {
        var id = (await CreateProductAsync(Input(sku: sku, name: "Part " + sku, reorderPoint: reorderPoint, reorderQuantity: reorderQuantity, costCents: costCents, categoryId: categoryId))).Id;
        if (onHand != 0)
        {
            Assert.True((await PostAsync(Receive(id, onHand))).Succeeded);
        }

        await PreferAsync(id, supplierId, costCents, minOrder);
        return id;
    }

    protected async Task PreferAsync(int productId, int supplierId, long costCents = 100, int minOrder = 1)
    {
        await using var context = Db.CreateContext();
        context.ProductSuppliers.Add(new ProductSupplier
        {
            ProductId = productId, SupplierId = supplierId, IsPreferred = true, UnitCostCents = costCents, MinOrderQuantity = minOrder,
        });
        await context.SaveChangesAsync();
    }

    /// <summary>Runs a report's CSV export into memory and returns the text (BOM included).</summary>
    protected async Task<string> CsvAsync(Func<ReportService, Stream, Task> write)
    {
        using var stream = new MemoryStream();
        await WithReportsAsync(async s =>
        {
            await write(s, stream);
            return 0;
        });
        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    protected static string[] CsvLines(string csv) => csv.TrimStart('\uFEFF').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    protected async Task SetUnitCostAsync(int productId, long costCents)
    {
        await using var context = Db.CreateContext();
        var product = await context.Products.FindAsync(productId);
        product!.UnitCostCents = costCents;
        await context.SaveChangesAsync();
    }
}
