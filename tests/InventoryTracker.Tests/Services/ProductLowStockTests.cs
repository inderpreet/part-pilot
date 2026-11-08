using InventoryTracker.Services;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-06-2: the part list and detail flag a part low only once what is on order is counted (F06 FR-4).</summary>
public sealed class ProductLowStockTests : ReportingTestBase
{
    private async Task<ProductListItem> ListedAsync(int id)
    {
        var (context, products, _) = NewRequest();
        await using (context)
        {
            var page = await products.SearchAsync(new ProductListQuery { PageSize = 200 });
            return page.Items.Single(p => p.Id == id);
        }
    }

    private async Task<ProductDetail> DetailAsync(int id)
    {
        var (context, products, _) = NewRequest();
        await using (context)
        {
            return (await products.GetAsync(id))!;
        }
    }

    [Fact]
    public async Task Stock_at_or_below_the_reorder_point_with_nothing_on_order_is_low()
    {
        var supplier = await NewSupplierAsync();
        var id = await LowPartAsync("LOW", supplier, onHand: 5, reorderPoint: 12);

        Assert.True((await ListedAsync(id)).IsLowStock);
        Assert.True((await DetailAsync(id)).IsLowStock);
    }

    [Fact]
    public async Task Stock_on_order_counts_so_five_on_hand_and_ten_on_order_against_twelve_is_not_low()
    {
        var supplier = await NewSupplierAsync();
        var id = await LowPartAsync("COVERED", supplier, onHand: 5, reorderPoint: 12);
        await SubmittedAsync(supplier, new PurchaseOrderLineInput(id, 10));

        Assert.False((await ListedAsync(id)).IsLowStock);
        Assert.False((await DetailAsync(id)).IsLowStock);
    }

    [Fact]
    public async Task A_draft_order_does_not_cover_a_low_part()
    {
        var supplier = await NewSupplierAsync();
        var id = await LowPartAsync("DRAFTED", supplier, onHand: 5, reorderPoint: 12);
        await DraftAsync(supplier, null, new PurchaseOrderLineInput(id, 10));

        Assert.True((await ListedAsync(id)).IsLowStock);
        Assert.True((await DetailAsync(id)).IsLowStock);
    }

    [Fact]
    public async Task Only_the_unreceived_part_of_a_partly_received_order_counts()
    {
        var supplier = await NewSupplierAsync();
        var id = await LowPartAsync("PARTLY", supplier, onHand: 0, reorderPoint: 12);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(id, 20));
        var line = (await GetOrderAsync(order.Id)).Lines.Single();
        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(line.Id, 15)])).Succeeded);

        // 15 on hand now and 5 still to come: 20 > 12.
        Assert.False((await ListedAsync(id)).IsLowStock);
    }

    [Fact]
    public async Task A_part_without_a_reorder_point_is_never_low()
    {
        var supplier = await NewSupplierAsync();
        var id = await LowPartAsync("NOPOINT", supplier, onHand: 0, reorderPoint: 0);

        Assert.False((await ListedAsync(id)).IsLowStock);
        Assert.False((await DetailAsync(id)).IsLowStock);
    }
}
