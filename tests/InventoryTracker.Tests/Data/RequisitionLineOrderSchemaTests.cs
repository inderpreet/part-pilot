using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-13-1: links from requisition lines to purchase order lines, and the cascades that release them.</summary>
public sealed class RequisitionLineOrderSchemaTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private string _userId = null!;
    private long _lineA;
    private long _lineB;
    private int _orderId;
    private long _poLineId;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "buyer", DisplayName = "Bea Buyer" };
        var product = new Product { Sku = "P-1", Name = "Widget" };
        var supplier = new Supplier { Name = "Acme" };
        context.AddRange(user, product, supplier);
        await context.SaveChangesAsync();

        var requisitionA = new Requisition { Number = "REQ-2026-0001", RequestedById = user.Id, Lines = [new RequisitionLine { ProductId = product.Id, QuantityRequested = 2 }] };
        var requisitionB = new Requisition { Number = "REQ-2026-0002", RequestedById = user.Id, Lines = [new RequisitionLine { ProductId = product.Id, QuantityRequested = 3 }] };
        var order = new PurchaseOrder
        {
            Number = "PO-2026-0001",
            SupplierId = supplier.Id,
            OrderDate = new DateOnly(2026, 6, 1),
            CreatedById = user.Id,
            Lines = [new PurchaseOrderLine { ProductId = product.Id, QuantityOrdered = 5, UnitCostCents = 100 }],
        };
        context.AddRange(requisitionA, requisitionB, order);
        await context.SaveChangesAsync();

        _userId = user.Id;
        _lineA = requisitionA.Lines[0].Id;
        _lineB = requisitionB.Lines[0].Id;
        _orderId = order.Id;
        _poLineId = order.Lines[0].Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private static string Message(Exception ex) => (ex.InnerException ?? ex).Message;

    private RequisitionLineOrder Link(long requisitionLineId, int quantity = 2) => new()
    {
        RequisitionLineId = requisitionLineId,
        PurchaseOrderLineId = _poLineId,
        QuantityOrdered = quantity,
        ConvertedById = _userId,
        ConvertedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private async Task SaveAsync(params RequisitionLineOrder[] links)
    {
        await using var context = _db.CreateContext();
        context.RequisitionLineOrders.AddRange(links);
        await context.SaveChangesAsync();
    }

    private async Task<int> LinkCountAsync()
    {
        await using var context = _db.CreateContext();
        return await context.RequisitionLineOrders.CountAsync();
    }

    [Fact]
    public async Task Fresh_database_has_the_table_and_no_pending_changes()
    {
        await using var context = _db.CreateContext();

        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='RequisitionLineOrders'"));
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_RequisitionLineOrders_PurchaseOrderLineId'"));
        Assert.False(context.Database.HasPendingModelChanges(), "Entities changed without a new migration.");
    }

    [Fact]
    public async Task Two_requisition_lines_can_share_one_purchase_order_line()
    {
        await SaveAsync(Link(_lineA), Link(_lineB, 3));

        Assert.Equal(2, await LinkCountAsync());
    }

    [Fact]
    public async Task A_requisition_line_is_linked_at_most_once()
    {
        await SaveAsync(Link(_lineA));

        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Link(_lineA)));

        Assert.Contains("UNIQUE", Message(error), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Quantity_must_be_at_least_one()
    {
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => SaveAsync(Link(_lineA, 0)));

        Assert.Contains("CK_RequisitionLineOrders_QuantityOrdered", Message(error));
    }

    [Fact]
    public async Task Removing_the_purchase_order_line_releases_the_requisition_line()
    {
        await SaveAsync(Link(_lineA));

        await using (var context = _db.CreateContext())
        {
            context.PurchaseOrderLines.Remove(await context.PurchaseOrderLines.SingleAsync());
            await context.SaveChangesAsync();
        }

        Assert.Equal(0, await LinkCountAsync());
        await using var read = _db.CreateContext();
        Assert.Equal(2, await read.RequisitionLines.CountAsync());
    }

    [Fact]
    public async Task Deleting_a_draft_order_removes_its_links()
    {
        await SaveAsync(Link(_lineA), Link(_lineB, 3));

        await using (var context = _db.CreateContext())
        {
            context.PurchaseOrders.Remove(await context.PurchaseOrders.SingleAsync(o => o.Id == _orderId));
            await context.SaveChangesAsync();
        }

        Assert.Equal(0, await LinkCountAsync());
    }

    [Fact]
    public async Task Migration_applies_on_the_previous_schema_and_can_be_rolled_back()
    {
        await using var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();
        await using var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options);
        var migrations = context.GetService<IMigrationsAssembly>().Migrations.Keys.Order().ToList();
        var index = migrations.FindIndex(m => m.EndsWith("_AddRequisitionLineOrders", StringComparison.Ordinal));
        Assert.True(index > 0);

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        var before = await SqliteTestDb.ScalarAsync(context, "SELECT sql FROM sqlite_master WHERE name = 'PurchaseOrderLines'");

        await context.Database.MigrateAsync();
        Assert.Empty(await context.Database.GetPendingMigrationsAsync());
        Assert.Equal(before, await SqliteTestDb.ScalarAsync(context, "SELECT sql FROM sqlite_master WHERE name = 'PurchaseOrderLines'"));
        Assert.Equal(2L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE type='trigger' AND name LIKE 'TR_StockTransactions_%'"));

        await context.GetService<IMigrator>().MigrateAsync(migrations[index - 1]);
        Assert.Equal(0L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'RequisitionLineOrders'"));
        await context.Database.MigrateAsync();
        Assert.Equal(1L, await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'RequisitionLineOrders'"));
    }
}
