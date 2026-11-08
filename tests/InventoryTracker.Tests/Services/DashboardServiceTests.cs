using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-06-2: dashboard tiles and lists.</summary>
public sealed class DashboardServiceTests : ReportingTestBase
{
    private DateOnly Today => DateOnly.FromDateTime(Clock.UtcNow.ToLocalTime());

    private async Task SetExpectedAsync(int orderId, DateOnly? expected)
    {
        await using var context = Db.CreateContext();
        await context.PurchaseOrders.Where(o => o.Id == orderId).ExecuteUpdateAsync(u => u.SetProperty(o => o.ExpectedDate, expected));
    }

    [Fact]
    public async Task An_empty_database_gives_zeros_and_empty_lists()
    {
        var summary = await WithDashboardAsync(s => s.GetAsync());

        Assert.Equal(new DashboardTiles(0, 0, 0, 0, 0, 0, 0), summary.Tiles);
        Assert.Empty(summary.LowStock);
        Assert.Empty(summary.RecentMovements);
        Assert.Empty(summary.DueThisWeek);
    }

    [Fact]
    public async Task Tiles_count_active_parts_value_low_and_out_of_stock()
    {
        var supplier = await NewSupplierAsync();
        await LowPartAsync("LOW", supplier, onHand: 2, reorderPoint: 5, costCents: 1000);   // value 2,000
        await LowPartAsync("ZERO", supplier, onHand: 0, reorderPoint: 5, costCents: 500);   // out of stock and low
        await LowPartAsync("OK", supplier, onHand: 10, reorderPoint: 5, costCents: 250);    // value 2,500
        var archived = await LowPartAsync("OLD", supplier, onHand: 4, reorderPoint: 5, costCents: 100);
        await ArchiveAsync(archived);

        var tiles = await WithDashboardAsync(s => s.GetTilesAsync());

        Assert.Equal(3, tiles.ActiveSkus);
        Assert.Equal(2_000 + 2_500, tiles.StockValueCents);
        Assert.Equal(2, tiles.LowStockCount);
        Assert.Equal(1, tiles.OutOfStockCount);
    }

    [Fact]
    public async Task Open_orders_are_counted_with_their_value_and_overdue_ones_are_flagged()
    {
        var supplier = await NewSupplierAsync();
        var part = await LowPartAsync("P", supplier, onHand: 100, reorderPoint: 1, costCents: 100);
        var overdue = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 10));       // 1,000
        var onTime = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 5));         // 500
        await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 1));                      // no expected date: not overdue
        await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 99));                   // a draft is not open
        await SetExpectedAsync(overdue.Id, Today.AddDays(-1));
        await SetExpectedAsync(onTime.Id, Today);

        var tiles = await WithDashboardAsync(s => s.GetTilesAsync());

        Assert.Equal(3, tiles.OpenPurchaseOrders);
        Assert.Equal(1_000 + 500 + 100, tiles.OpenPurchaseOrderValueCents);
        Assert.Equal(1, tiles.OverduePurchaseOrders);
    }

    [Fact]
    public async Task Low_stock_list_is_the_most_urgent_first_and_capped()
    {
        var supplier = await NewSupplierAsync();
        for (var i = 1; i <= 12; i++)
        {
            await LowPartAsync($"L{i:D2}", supplier, onHand: i % 10, reorderPoint: 20);
        }

        var list = await WithDashboardAsync(s => s.GetLowStockAsync());

        Assert.Equal(DashboardService.ListSize, list.Count);
        var gaps = list.Select(r => r.OnHand + r.OnOrder - r.ReorderPoint).ToList();
        Assert.Equal(gaps.Order(), gaps);
    }

    [Fact]
    public async Task Recent_movements_are_newest_first_with_part_and_user()
    {
        var supplier = await NewSupplierAsync();
        var part = await LowPartAsync("MOVER", supplier);
        for (var i = 1; i <= 12; i++)
        {
            Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
            Assert.True((await PostAsync(Receive(part, i))).Succeeded);
        }

        var list = await WithDashboardAsync(s => s.GetRecentMovementsAsync());

        Assert.Equal(10, list.Count);
        Assert.Equal(12, list[0].QuantityDelta);
        Assert.Equal(3, list[^1].QuantityDelta);
        Assert.Equal("MOVER", list[0].Sku);
        Assert.Equal("Mo Ver", list[0].UserName);
    }

    [Fact]
    public async Task Due_this_week_lists_open_orders_expected_from_today_to_seven_days_out_soonest_first()
    {
        var supplier = await NewSupplierAsync("Acme");
        var part = await LowPartAsync("P", supplier, onHand: 100, reorderPoint: 1, costCents: 100);
        var yesterday = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 1));
        var today = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 2));
        var day7 = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 3));
        var day8 = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 4));
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 5));
        await SetExpectedAsync(yesterday.Id, Today.AddDays(-1));
        await SetExpectedAsync(today.Id, Today);
        await SetExpectedAsync(day7.Id, Today.AddDays(7));
        await SetExpectedAsync(day8.Id, Today.AddDays(8));
        await SetExpectedAsync(draft.Id, Today);

        var list = await WithDashboardAsync(s => s.GetDueThisWeekAsync());

        Assert.Equal([today.Id, day7.Id], list.Select(o => o.Id));
        Assert.Equal("Acme", list[0].SupplierName);
        Assert.Equal(200L, list[0].OutstandingCents);
    }

    [Fact]
    public async Task A_partly_received_order_shows_only_what_is_outstanding()
    {
        var supplier = await NewSupplierAsync();
        var part = await LowPartAsync("P", supplier, reorderPoint: 1, costCents: 100);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 10));
        await SetExpectedAsync(order.Id, Today.AddDays(1));
        var line = (await GetOrderAsync(order.Id)).Lines.Single();
        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(line.Id, 4)])).Succeeded);

        var due = Assert.Single(await WithDashboardAsync(s => s.GetDueThisWeekAsync()));

        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, due.Status);
        Assert.Equal(6 * 100L, due.OutstandingCents);
    }
}
