using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>Collects every purchase order event; optionally throws, to prove a failing handler cannot break the operation.</summary>
public sealed class RecordingPurchaseOrderEvents : IPurchaseOrderEvents
{
    public List<PurchaseOrderEvent> Events { get; } = [];

    public bool Throw { get; set; }

    public Task HandleAsync(PurchaseOrderEvent purchaseOrderEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(purchaseOrderEvent);
        return Throw ? throw new InvalidOperationException("handler failed") : Task.CompletedTask;
    }
}

/// <summary>Stock test database plus suppliers, links and purchase order helpers.</summary>
public abstract class PurchaseOrderTestBase : StockTestBase
{
    protected static readonly DateOnly Jan5 = new(2026, 1, 5);

    protected RecordingPurchaseOrderEvents Events { get; } = new();

    protected PurchaseOrderActor Actor => new(UserId);

    protected PurchaseOrderActor AdminActor => new(UserId, IsAdmin: true);

    /// <summary>A new context + purchase order service, like one web request.</summary>
    protected (AppDbContext Context, PurchaseOrderService Orders) NewOrders()
    {
        var (context, stock) = NewStock();
        var publisher = new PurchaseOrderEventPublisher([Events], NullLogger<PurchaseOrderEventPublisher>.Instance);
        return (context, new PurchaseOrderService(context, stock, Settings, Clock, publisher));
    }

    protected async Task<int> NewSupplierAsync(string name = "Acme", bool archived = false)
    {
        await using var context = Db.CreateContext();
        var supplier = new Supplier { Name = name, IsActive = !archived };
        context.Suppliers.Add(supplier);
        await context.SaveChangesAsync();
        return supplier.Id;
    }

    protected async Task LinkAsync(int productId, int supplierId, long costCents, int? leadTimeDays = null, string? supplierSku = null, int minOrder = 1)
    {
        await using var context = Db.CreateContext();
        context.ProductSuppliers.Add(new ProductSupplier
        {
            ProductId = productId,
            SupplierId = supplierId,
            UnitCostCents = costCents,
            LeadTimeDays = leadTimeDays,
            SupplierSku = supplierSku,
            MinOrderQuantity = minOrder,
        });
        await context.SaveChangesAsync();
    }

    protected async Task<PurchaseOrderDetail> GetOrderAsync(int id)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            return (await orders.GetAsync(id))!;
        }
    }

    protected async Task<PurchaseOrderDetail> DraftAsync(int supplierId, DateOnly? date = null, params PurchaseOrderLineInput[] lines)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            var result = await orders.CreateDraftAsync(supplierId, Actor, lines, date ?? Jan5);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            return result.Value;
        }
    }

    protected async Task<PurchaseOrderDetail> SubmittedAsync(int supplierId, params PurchaseOrderLineInput[] lines)
    {
        var draft = await DraftAsync(supplierId, null, lines);
        var (context, orders) = NewOrders();
        await using (context)
        {
            var result = await orders.SubmitAsync(draft.Id, Actor);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
            return result.Value;
        }
    }

    protected async Task<Result<ReceiptResult>> ReceiveAsync(
        int orderId,
        ReceiveLineInput[] lines,
        string? reference = null,
        string? note = null,
        bool allowOver = false,
        PurchaseOrderActor? actor = null,
        long? rowVersion = null)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            return await orders.ReceiveAsync(orderId, lines, actor ?? Actor, reference, note, allowOver, rowVersion);
        }
    }

    protected async Task<Result<PostedMovement>> ReverseReceiptAsync(long transactionId, string? note = null)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            return await orders.ReverseReceiptAsync(transactionId, Actor, note);
        }
    }

    protected async Task<Result> RunAsync(Func<PurchaseOrderService, Task<Result>> action)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            return await action(orders);
        }
    }

    protected async Task<T> QueryAsync<T>(Func<PurchaseOrderService, Task<T>> query)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            return await query(orders);
        }
    }

    protected static string Messages(Result result) => string.Join("; ", result.Errors.Select(e => e.Message));
}
