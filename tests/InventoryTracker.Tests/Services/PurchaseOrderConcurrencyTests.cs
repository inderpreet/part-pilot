using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>
/// Real file database, one connection per "request", configured as in production (WAL, busy timeout, immediate transactions):
/// concurrent numbering and receiving must never duplicate a number or receive more than was ordered.
/// </summary>
public sealed class PurchaseOrderConcurrencyTests : IAsyncLifetime
{
    private string _path = null!;
    private string _userId = null!;
    private int _productId;
    private int _supplierId;
    private readonly FakeSettings _settings = new();
    private readonly TestClock _clock = new(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc));

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"inventory-po-{Guid.NewGuid():N}.db");
        await using var context = NewContext();
        await context.Database.MigrateAsync();

        var user = new ApplicationUser { UserName = "buyer", DisplayName = "Bea Buyer" };
        var product = new Product { Sku = "RACE-1", Name = "Racing widget" };
        var supplier = new Supplier { Name = "Acme" };
        context.AddRange(user, product, supplier);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
        _supplierId = supplier.Id;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    private AppDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseInventorySqlite($"Data Source={_path}");
        return new AppDbContext(builder.Options);
    }

    private PurchaseOrderService NewOrders(AppDbContext context)
    {
        var stock = new StockService(context, _settings, _clock, NullLogger<StockService>.Instance);
        var publisher = new PurchaseOrderEventPublisher([], NullLogger<PurchaseOrderEventPublisher>.Instance);
        return new PurchaseOrderService(context, stock, _settings, _clock, publisher);
    }

    private PurchaseOrderActor Actor => new(_userId);

    private static async Task<T[]> RunConcurrentlyAsync<T>(int workers, Func<int, Task<T>> work)
    {
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, workers).Select(i => Task.Run(async () =>
        {
            await gate.Task;
            return await work(i);
        })).ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Concurrent_drafts_get_distinct_consecutive_numbers()
    {
        const int workers = 10;

        var results = await RunConcurrentlyAsync(workers, async _ =>
        {
            await using var context = NewContext();
            return await NewOrders(context).CreateDraftAsync(_supplierId, Actor, orderDate: new DateOnly(2026, 6, 1));
        });

        Assert.All(results, r => Assert.True(r.Succeeded, string.Join("; ", r.Errors.Select(e => e.Message))));
        var numbers = results.Select(r => r.Value.Number).Order().ToList();
        Assert.Equal(Enumerable.Range(1, workers).Select(i => $"PO-2026-{i:D4}"), numbers);

        await using var read = NewContext();
        Assert.Equal(workers + 1, (await read.DocumentSequences.SingleAsync(s => s.Name == "PurchaseOrder" && s.Year == 2026)).NextValue);
    }

    [Fact]
    public async Task Concurrent_receipts_never_receive_more_than_was_ordered()
    {
        int orderId;
        long lineId;
        await using (var context = NewContext())
        {
            var orders = NewOrders(context);
            var draft = await orders.CreateDraftAsync(_supplierId, Actor, [new PurchaseOrderLineInput(_productId, 10, 100)], new DateOnly(2026, 6, 1));
            Assert.True((await orders.SubmitAsync(draft.Value.Id, Actor)).Succeeded);
            orderId = draft.Value.Id;
            lineId = draft.Value.Lines[0].Id;
        }

        // Eight receivers of 3 each, 10 ordered: exactly three can succeed (9), the rest see only 1 outstanding.
        var results = await RunConcurrentlyAsync(8, async _ =>
        {
            await using var context = NewContext();
            return await NewOrders(context).ReceiveAsync(orderId, [new ReceiveLineInput(lineId, 3)], Actor);
        });

        Assert.Equal(3, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.Errors, e => e.Code == "po.over_receipt"));

        await using var read = NewContext();
        var line = await read.PurchaseOrderLines.SingleAsync(l => l.Id == lineId);
        Assert.Equal(9, line.QuantityReceived);
        Assert.Equal(9, (await read.Products.SingleAsync(p => p.Id == _productId)).QuantityOnHand);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, (await read.PurchaseOrders.SingleAsync(p => p.Id == orderId)).Status);
        Assert.Equal(9, await read.StockTransactions.Where(t => t.PurchaseOrderLineId == lineId).SumAsync(t => t.QuantityDelta));
    }

    [Fact]
    public async Task Concurrent_submit_and_cancel_end_cancelled_whichever_goes_first()
    {
        int orderId;
        await using (var context = NewContext())
        {
            var draft = await NewOrders(context).CreateDraftAsync(_supplierId, Actor, [new PurchaseOrderLineInput(_productId, 1, 100)], new DateOnly(2026, 6, 1));
            orderId = draft.Value.Id;
        }

        var results = await RunConcurrentlyAsync(2, async worker =>
        {
            await using var context = NewContext();
            var orders = NewOrders(context);
            return worker == 0 ? (await orders.SubmitAsync(orderId, Actor)).Succeeded : (await orders.CancelAsync(orderId, Actor)).Succeeded;
        });

        // Submit-then-cancel and cancel-then-submit both end Cancelled; the only difference is whether the submit got in first.
        Assert.True(results[1], "Cancel is allowed from both Draft and Submitted.");
        await using var read = NewContext();
        Assert.Equal(PurchaseOrderStatus.Cancelled, (await read.PurchaseOrders.SingleAsync(p => p.Id == orderId)).Status);
    }
}