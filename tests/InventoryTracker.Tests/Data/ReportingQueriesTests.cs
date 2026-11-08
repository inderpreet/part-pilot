using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-06-1: the shared on-order and low-stock queries.</summary>
public sealed class ReportingQueriesTests : IAsyncLifetime
{
    private SqliteTestDb _db = null!;
    private string _userId = null!;
    private int _supplierId;
    private int _otherSupplierId;
    private int _poCounter;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
        await using var context = _db.CreateContext();
        var user = new ApplicationUser { UserName = "buyer", DisplayName = "Buyer" };
        var supplier = new Supplier { Name = "Acme" };
        var other = new Supplier { Name = "Bolt Co" };
        context.AddRange(user, supplier, other);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _supplierId = supplier.Id;
        _otherSupplierId = other.Id;
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task<int> AddProductAsync(string sku, int onHand = 0, int reorderPoint = 0, int reorderQuantity = 0, bool active = true, int? categoryId = null)
    {
        await using var context = _db.CreateContext();
        var product = new Product
        {
            Sku = sku, Name = sku, QuantityOnHand = onHand, ReorderPoint = reorderPoint,
            ReorderQuantity = reorderQuantity, IsActive = active, CategoryId = categoryId,
        };
        context.Products.Add(product);
        await context.SaveChangesAsync();
        return product.Id;
    }

    private async Task AddOrderAsync(PurchaseOrderStatus status, params (int ProductId, int Ordered, int Received)[] lines)
    {
        await using var context = _db.CreateContext();
        var order = new PurchaseOrder
        {
            Number = $"PO-T-{++_poCounter:D4}", SupplierId = _supplierId, Status = status,
            OrderDate = new DateOnly(2026, 11, 1), CreatedById = _userId,
        };
        foreach (var (productId, ordered, received) in lines)
        {
            order.Lines.Add(new PurchaseOrderLine { ProductId = productId, QuantityOrdered = ordered, QuantityReceived = received, UnitCostCents = 100 });
        }

        context.PurchaseOrders.Add(order);
        await context.SaveChangesAsync();
    }

    private async Task LinkAsync(int productId, int supplierId, bool preferred, int minOrder = 1, long cost = 250)
    {
        await using var context = _db.CreateContext();
        context.ProductSuppliers.Add(new ProductSupplier
        {
            ProductId = productId, SupplierId = supplierId, IsPreferred = preferred, MinOrderQuantity = minOrder, UnitCostCents = cost,
        });
        await context.SaveChangesAsync();
    }

    private async Task<Dictionary<int, int>> OnOrderAsync()
    {
        await using var context = _db.CreateContext();
        return (await context.OnOrder().ToListAsync()).ToDictionary(r => r.ProductId, r => r.Quantity);
    }

    private async Task<List<LowStockRow>> LowStockAsync()
    {
        await using var context = _db.CreateContext();
        return await context.LowStock().OrderBy(r => r.Sku).ToListAsync();
    }

    [Fact]
    public async Task On_order_is_the_unreceived_part_of_open_orders_summed_over_orders()
    {
        var a = await AddProductAsync("A");
        var b = await AddProductAsync("B");
        await AddOrderAsync(PurchaseOrderStatus.Submitted, (a, 10, 0), (b, 4, 0));
        await AddOrderAsync(PurchaseOrderStatus.PartiallyReceived, (a, 20, 15));

        var onOrder = await OnOrderAsync();

        Assert.Equal(15, onOrder[a]);
        Assert.Equal(4, onOrder[b]);
    }

    [Theory]
    [InlineData(PurchaseOrderStatus.Draft)]
    [InlineData(PurchaseOrderStatus.Received)]
    [InlineData(PurchaseOrderStatus.Cancelled)]
    public async Task On_order_ignores_orders_that_are_not_open(PurchaseOrderStatus status)
    {
        var a = await AddProductAsync("A");
        await AddOrderAsync(status, (a, 10, status == PurchaseOrderStatus.Received ? 6 : 0));

        Assert.Empty(await OnOrderAsync());
    }

    [Fact]
    public async Task On_order_skips_lines_that_are_fully_received()
    {
        var a = await AddProductAsync("A");
        var b = await AddProductAsync("B");
        await AddOrderAsync(PurchaseOrderStatus.PartiallyReceived, (a, 10, 10), (b, 10, 3));

        var onOrder = await OnOrderAsync();

        Assert.False(onOrder.ContainsKey(a));
        Assert.Equal(7, onOrder[b]);
    }

    [Fact]
    public async Task Stock_below_the_reorder_point_with_nothing_on_order_is_low()
    {
        var id = await AddProductAsync("LOW", onHand: 5, reorderPoint: 12, reorderQuantity: 20);

        var row = Assert.Single(await LowStockAsync());

        Assert.Equal(id, row.ProductId);
        Assert.Equal(5, row.OnHand);
        Assert.Equal(0, row.OnOrder);
        Assert.Equal(12, row.ReorderPoint);
        Assert.Equal(20, row.ReorderQuantity);
    }

    [Fact]
    public async Task What_is_on_order_counts_towards_the_reorder_point()
    {
        // F06 acceptance: on hand 5 + on order 10 against a reorder point of 12 is not low.
        var id = await AddProductAsync("COVERED", onHand: 5, reorderPoint: 12);
        await AddOrderAsync(PurchaseOrderStatus.Submitted, (id, 10, 0));

        Assert.Empty(await LowStockAsync());
    }

    [Fact]
    public async Task On_hand_plus_on_order_equal_to_the_reorder_point_is_still_low()
    {
        var id = await AddProductAsync("EDGE", onHand: 5, reorderPoint: 12);
        await AddOrderAsync(PurchaseOrderStatus.Submitted, (id, 7, 0));

        var row = Assert.Single(await LowStockAsync());

        Assert.Equal(7, row.OnOrder);
    }

    [Fact]
    public async Task Only_the_open_part_of_an_order_counts()
    {
        var id = await AddProductAsync("PART", onHand: 0, reorderPoint: 10);
        await AddOrderAsync(PurchaseOrderStatus.PartiallyReceived, (id, 10, 8));

        var row = Assert.Single(await LowStockAsync());

        Assert.Equal(2, row.OnOrder);
    }

    [Fact]
    public async Task A_draft_order_does_not_hide_a_low_product()
    {
        var id = await AddProductAsync("DRAFTED", onHand: 1, reorderPoint: 10);
        await AddOrderAsync(PurchaseOrderStatus.Draft, (id, 50, 0));

        var row = Assert.Single(await LowStockAsync());

        Assert.Equal(0, row.OnOrder);
    }

    [Fact]
    public async Task Products_without_a_reorder_point_or_archived_ones_are_never_low()
    {
        await AddProductAsync("NOPOINT", onHand: 0, reorderPoint: 0);
        await AddProductAsync("ARCHIVED", onHand: 0, reorderPoint: 10, active: false);
        await AddProductAsync("FINE", onHand: 50, reorderPoint: 10);

        Assert.Empty(await LowStockAsync());
    }

    [Fact]
    public async Task A_product_that_is_out_of_stock_is_low()
    {
        await AddProductAsync("ZERO", onHand: 0, reorderPoint: 3);

        Assert.Single(await LowStockAsync());
    }

    [Fact]
    public async Task Each_low_product_appears_once_whatever_its_orders_and_suppliers()
    {
        var id = await AddProductAsync("MANY", onHand: 0, reorderPoint: 100);
        await AddOrderAsync(PurchaseOrderStatus.Submitted, (id, 2, 0));
        await AddOrderAsync(PurchaseOrderStatus.Submitted, (id, 3, 0));
        await LinkAsync(id, _supplierId, preferred: true);
        await LinkAsync(id, _otherSupplierId, preferred: false);

        var row = Assert.Single(await LowStockAsync());

        Assert.Equal(5, row.OnOrder);
        Assert.Equal(_supplierId, row.PreferredSupplierId);
    }

    [Fact]
    public async Task The_row_carries_the_preferred_supplier_with_its_minimum_and_price()
    {
        var id = await AddProductAsync("PREF", onHand: 0, reorderPoint: 10);
        await LinkAsync(id, _supplierId, preferred: false, minOrder: 5, cost: 100);
        await LinkAsync(id, _otherSupplierId, preferred: true, minOrder: 25, cost: 300);

        var row = Assert.Single(await LowStockAsync());

        Assert.Equal(_otherSupplierId, row.PreferredSupplierId);
        Assert.Equal("Bolt Co", row.PreferredSupplierName);
        Assert.Equal(25, row.PreferredMinOrderQuantity);
        Assert.Equal(300L, row.PreferredUnitCostCents);
    }

    [Fact]
    public async Task Without_a_preferred_supplier_those_fields_are_empty()
    {
        var id = await AddProductAsync("NOPREF", onHand: 0, reorderPoint: 10);
        await LinkAsync(id, _supplierId, preferred: false);

        var row = Assert.Single(await LowStockAsync());

        Assert.Null(row.PreferredSupplierId);
        Assert.Null(row.PreferredSupplierName);
        Assert.Null(row.PreferredMinOrderQuantity);
        Assert.Null(row.PreferredUnitCostCents);
    }

    [Fact]
    public async Task The_category_name_comes_along_and_is_null_without_a_category()
    {
        int categoryId;
        await using (var context = _db.CreateContext())
        {
            var category = new Category { Name = "Fasteners" };
            context.Categories.Add(category);
            await context.SaveChangesAsync();
            categoryId = category.Id;
        }

        await AddProductAsync("CAT", onHand: 0, reorderPoint: 5, categoryId: categoryId);
        await AddProductAsync("NOCAT", onHand: 0, reorderPoint: 5);

        var rows = await LowStockAsync();

        Assert.Equal("Fasteners", rows.Single(r => r.Sku == "CAT").CategoryName);
        Assert.Null(rows.Single(r => r.Sku == "NOCAT").CategoryName);
    }

    [Fact]
    public async Task Low_stock_can_be_filtered_counted_and_paged_in_the_database()
    {
        for (var i = 1; i <= 7; i++)
        {
            await AddProductAsync($"L{i}", onHand: 0, reorderPoint: 5);
        }

        await AddProductAsync("OK", onHand: 100, reorderPoint: 5);
        await using var context = _db.CreateContext();

        var count = await context.LowStock().CountAsync();
        var page = await context.LowStock().OrderBy(r => r.Name).Skip(3).Take(2).ToListAsync();

        Assert.Equal(7, count);
        Assert.Equal(["L4", "L5"], page.Select(r => r.Sku));
    }
}
