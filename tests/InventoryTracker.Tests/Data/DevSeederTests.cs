using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Data;

/// <summary>WI-06-1: the development demo seeder produces data the app's own invariants accept.</summary>
public sealed class DevSeederTests : IAsyncLifetime
{
    private static readonly DemoSeedOptions Small = new(Products: 300, Suppliers: 12, PurchaseOrders: 80, Transactions: 6_000);

    private static readonly FakeClock Clock = new(new DateTimeOffset(2026, 11, 8, 12, 0, 0, TimeSpan.Zero));

    private SqliteTestDb _db = null!;

    public async Task InitializeAsync()
    {
        _db = await SqliteTestDb.CreateAsync();
    }

    public async Task DisposeAsync() => await _db.DisposeAsync();

    private async Task AddUserAsync()
    {
        await using var context = _db.CreateContext();
        context.Users.Add(new ApplicationUser { UserName = "admin", DisplayName = "Admin" });
        await context.SaveChangesAsync();
    }

    private async Task<DemoSeedResult> SeedAsync(DemoSeedOptions? options = null)
    {
        await using var context = _db.CreateContext();
        return await new DevSeeder(context, Clock).SeedAsync(options ?? Small);
    }

    [Fact]
    public async Task Without_an_account_it_does_nothing_and_says_why()
    {
        var result = await SeedAsync();

        Assert.False(result.Seeded);
        Assert.Contains("first account", result.Message);
        await using var context = _db.CreateContext();
        Assert.Empty(context.Products);
    }

    [Fact]
    public async Task Creates_the_requested_amounts()
    {
        await AddUserAsync();

        var result = await SeedAsync();

        Assert.True(result.Seeded);
        Assert.Equal(300, result.Products);
        Assert.Equal(6_000, result.Transactions);
        await using var context = _db.CreateContext();
        Assert.Equal(300, await context.Products.CountAsync());
        Assert.Equal(12, await context.Suppliers.CountAsync());
        Assert.Equal(80, await context.PurchaseOrders.CountAsync());
        Assert.Equal(6_000, await context.StockTransactions.CountAsync());
        Assert.All(await context.Products.Select(p => p.Sku).ToListAsync(), sku => Assert.StartsWith(DevSeeder.Prefix, sku));
    }

    [Fact]
    public async Task A_second_run_does_nothing()
    {
        await AddUserAsync();
        await SeedAsync();

        var second = await SeedAsync();

        Assert.False(second.Seeded);
        await using var context = _db.CreateContext();
        Assert.Equal(300, await context.Products.CountAsync());
    }

    [Fact]
    public async Task The_same_options_give_the_same_data()
    {
        await AddUserAsync();
        await SeedAsync();
        await using var other = await SqliteTestDb.CreateAsync();
        await using (var context = other.CreateContext())
        {
            context.Users.Add(new ApplicationUser { UserName = "admin", DisplayName = "Admin" });
            await context.SaveChangesAsync();
            await new DevSeeder(context, Clock).SeedAsync(Small);
        }

        await using var a = _db.CreateContext();
        await using var b = other.CreateContext();
        Assert.Equal(
            await a.Products.OrderBy(p => p.Sku).Select(p => p.Name + "|" + p.QuantityOnHand).ToListAsync(),
            await b.Products.OrderBy(p => p.Sku).Select(p => p.Name + "|" + p.QuantityOnHand).ToListAsync());
    }

    [Fact]
    public async Task Every_product_quantity_is_the_sum_of_its_ledger_and_never_negative()
    {
        await AddUserAsync();
        await SeedAsync();
        await using var context = _db.CreateContext();

        var mismatches = await SqliteTestDb.ScalarAsync(context,
            """
            SELECT COUNT(*) FROM "Products" p
            WHERE p."QuantityOnHand" <> COALESCE((SELECT SUM("QuantityDelta") FROM "StockTransactions" t WHERE t."ProductId" = p."Id"), 0)
            """);
        var negative = await SqliteTestDb.ScalarAsync(context, "SELECT COUNT(*) FROM \"StockTransactions\" WHERE \"QuantityAfter\" < 0");

        Assert.Equal(0L, mismatches);
        Assert.Equal(0L, negative);
    }

    [Fact]
    public async Task QuantityAfter_follows_posting_order()
    {
        await AddUserAsync();
        await SeedAsync();
        await using var context = _db.CreateContext();

        var wrong = await SqliteTestDb.ScalarAsync(context,
            """
            SELECT COUNT(*) FROM (
                SELECT "QuantityAfter" - "QuantityDelta" AS Before,
                       LAG("QuantityAfter", 1, 0) OVER (PARTITION BY "ProductId" ORDER BY "Id") AS Previous
                FROM "StockTransactions") WHERE Before <> Previous
            """);

        Assert.Equal(0L, wrong);
    }

    [Fact]
    public async Task Received_quantities_on_order_lines_match_their_receipts()
    {
        await AddUserAsync();
        await SeedAsync();
        await using var context = _db.CreateContext();

        var mismatches = await SqliteTestDb.ScalarAsync(context,
            """
            SELECT COUNT(*) FROM "PurchaseOrderLines" l
            WHERE l."QuantityReceived" <> COALESCE((SELECT SUM("QuantityDelta") FROM "StockTransactions" t WHERE t."PurchaseOrderLineId" = l."Id"), 0)
            """);

        Assert.Equal(0L, mismatches);
    }

    [Fact]
    public async Task The_data_has_something_for_every_dashboard_list()
    {
        await AddUserAsync();
        await SeedAsync();
        await using var context = _db.CreateContext();

        Assert.NotEmpty(await context.LowStock().ToListAsync());
        Assert.NotEmpty(await context.OnOrder().ToListAsync());
        Assert.True(await context.PurchaseOrders.AnyAsync(p => p.Status == PurchaseOrderStatus.Draft));
        Assert.True(await context.PurchaseOrders.AnyAsync(p => p.Status == PurchaseOrderStatus.Received));
        Assert.True(await context.Products.AnyAsync(p => p.QuantityOnHand == 0 && p.IsActive));
        Assert.True(await context.ProductSuppliers.AnyAsync(s => s.IsPreferred));
    }
}
