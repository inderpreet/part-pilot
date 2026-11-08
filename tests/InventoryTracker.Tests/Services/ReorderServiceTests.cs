using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-06-2: the suggested-quantity rule and the low-stock to draft purchase order flow.</summary>
public sealed class ReorderServiceTests : ReportingTestBase
{
    [Theory]
    [InlineData(5, 0, 12, 0, null, 7)]      // F06 acceptance: max(ReorderQuantity, 12 - 5)
    [InlineData(5, 0, 12, 20, null, 20)]    // reorder quantity larger than the gap
    [InlineData(0, 0, 100, 20, null, 100)]  // the gap larger than the reorder quantity
    [InlineData(5, 4, 12, 0, null, 3)]      // on order offsets the gap
    [InlineData(12, 0, 12, 0, null, 1)]     // exactly at the point: never suggest zero
    [InlineData(-3, 0, 5, 0, null, 8)]      // negative stock widens the gap
    [InlineData(5, 0, 12, 0, 25, 25)]       // raised to the supplier's minimum
    [InlineData(5, 0, 12, 40, 25, 40)]      // already above the minimum
    [InlineData(5, 0, 12, 0, 1, 7)]         // a minimum of 1 changes nothing
    public void The_suggested_quantity_follows_the_rule(int onHand, int onOrder, int reorderPoint, int reorderQuantity, int? minOrder, int expected)
        => Assert.Equal(expected, ReorderMath.SuggestedQuantity(onHand, onOrder, reorderPoint, reorderQuantity, minOrder));

    [Fact]
    public async Task A_low_part_is_suggested_with_the_acceptance_quantity_and_its_supplier()
    {
        var supplier = await NewSupplierAsync("Acme");
        await LowPartAsync("LOW", supplier, onHand: 5, reorderPoint: 12, costCents: 250);

        var page = await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter()));

        var item = Assert.Single(page.Items);
        Assert.Equal(5, item.OnHand);
        Assert.Equal(0, item.OnOrder);
        Assert.Equal(7, item.SuggestedQuantity);
        Assert.Equal("Acme", item.SupplierName);
        Assert.Equal(250L, item.UnitCostCents);
        Assert.True(item.CanOrder);
    }

    [Fact]
    public async Task A_part_covered_by_an_open_order_is_not_suggested_and_a_partly_covered_one_needs_less()
    {
        var supplier = await NewSupplierAsync();
        var covered = await LowPartAsync("COVERED", supplier, onHand: 5, reorderPoint: 12);
        var partly = await LowPartAsync("PARTLY", supplier, onHand: 5, reorderPoint: 12);
        await SubmittedAsync(supplier, new PurchaseOrderLineInput(covered, 10), new PurchaseOrderLineInput(partly, 4));

        var page = await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter()));

        var item = Assert.Single(page.Items);
        Assert.Equal(partly, item.ProductId);
        Assert.Equal(4, item.OnOrder);
        Assert.Equal(3, item.SuggestedQuantity);
    }

    [Fact]
    public async Task What_sits_on_draft_orders_is_shown_but_does_not_count_as_ordered()
    {
        var supplier = await NewSupplierAsync();
        var part = await LowPartAsync("DRAFTED", supplier, onHand: 0, reorderPoint: 10);
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 6));

        var item = Assert.Single((await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter()))).Items);

        Assert.Equal(0, item.OnOrder);
        Assert.Equal(10, item.SuggestedQuantity);
        Assert.Equal(6, item.InDraft);
        Assert.Equal(draft.Id, Assert.Single(item.Drafts).PurchaseOrderId);
    }

    [Fact]
    public async Task The_minimum_order_quantity_raises_the_suggestion()
    {
        var supplier = await NewSupplierAsync();
        await LowPartAsync("MIN", supplier, onHand: 5, reorderPoint: 12, minOrder: 50);

        var item = Assert.Single((await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter()))).Items);

        Assert.Equal(50, item.SuggestedQuantity);
    }

    [Fact]
    public async Task Suggestions_filter_by_category_preferred_supplier_and_search_and_sort_by_urgency()
    {
        var acme = await NewSupplierAsync("Acme");
        var bolt = await NewSupplierAsync("Bolt Co");
        var fixings = (await CreateCategoryAsync("Fixings")).Id;
        await LowPartAsync("A-1", acme, onHand: 9, reorderPoint: 10, categoryId: fixings);
        await LowPartAsync("A-2", acme, onHand: 0, reorderPoint: 10);
        await LowPartAsync("B-1", bolt, onHand: 5, reorderPoint: 10, categoryId: fixings);
        await LowPartAsync("OK", acme, onHand: 50, reorderPoint: 10);

        var all = await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter()));
        var byCategory = await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter { CategoryId = fixings }));
        var bySupplier = await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter { SupplierId = acme }));
        var bySearch = await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter { Search = "b-1" }));

        Assert.Equal(["A-2", "B-1", "A-1"], all.Items.Select(i => i.Sku));
        Assert.Equal(["B-1", "A-1"], byCategory.Items.Select(i => i.Sku));
        Assert.Equal(["A-2", "A-1"], bySupplier.Items.Select(i => i.Sku));
        Assert.Equal(["B-1"], bySearch.Items.Select(i => i.Sku));
    }

    [Fact]
    public async Task A_part_without_a_preferred_supplier_or_with_an_archived_one_cannot_be_ordered()
    {
        var archived = await NewSupplierAsync("Gone");
        await LowPartAsync("ARCH", archived, onHand: 0, reorderPoint: 5);
        await using (var context = Db.CreateContext())
        {
            await context.Suppliers.Where(s => s.Id == archived).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, false));
        }

        await CreateProductAsync(Input(sku: "NOSUP", reorderPoint: 5));

        var items = (await WithReorderAsync(s => s.GetSuggestionsAsync(new ReorderFilter { Sort = "sku" }))).Items;

        Assert.Equal(["ARCH", "NOSUP"], items.Select(i => i.Sku));
        Assert.All(items, i => Assert.False(i.CanOrder));
    }

    [Fact]
    public async Task Five_suggestions_across_two_suppliers_make_two_draft_orders_with_the_supplier_prices()
    {
        var acme = await NewSupplierAsync("Acme");
        var bolt = await NewSupplierAsync("Bolt Co");
        var a1 = await LowPartAsync("A-1", acme, costCents: 100);
        var a2 = await LowPartAsync("A-2", acme, costCents: 200);
        var a3 = await LowPartAsync("A-3", acme, costCents: 300);
        var b1 = await LowPartAsync("B-1", bolt, costCents: 400);
        var b2 = await LowPartAsync("B-2", bolt, costCents: 500);

        var result = await WithReorderAsync(s => s.CreateDraftPosAsync(
            [new(a1, 10), new(a2, 5), new(a3, 1), new(b1, 2), new(b2, 3)], Actor));

        Assert.Empty(result.Skipped);
        Assert.Equal(["Acme", "Bolt Co"], result.Created.Select(c => c.SupplierName));
        Assert.Equal([3, 2], result.Created.Select(c => c.LineCount));
        Assert.Equal(10 * 100L + 5 * 200 + 1 * 300, result.Created[0].TotalCents);
        Assert.Equal(2 * 400L + 3 * 500, result.Created[1].TotalCents);

        foreach (var created in result.Created)
        {
            var order = await GetOrderAsync(created.PurchaseOrderId);
            Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
            Assert.Equal(created.SupplierId, order.SupplierId);
        }
    }

    [Fact]
    public async Task Items_that_cannot_be_ordered_are_reported_while_the_rest_are_ordered()
    {
        var acme = await NewSupplierAsync("Acme");
        var good = await LowPartAsync("GOOD", acme);
        var noSupplier = (await CreateProductAsync(Input(sku: "NOSUP", reorderPoint: 5))).Id;
        var zero = await LowPartAsync("ZERO", acme);
        var archivedPart = await LowPartAsync("OLD", acme);
        await ArchiveAsync(archivedPart);

        var result = await WithReorderAsync(s => s.CreateDraftPosAsync(
            [new(good, 4), new(noSupplier, 4), new(zero, 0), new(archivedPart, 4), new(99999, 4)], Actor));

        var order = Assert.Single(result.Created);
        Assert.Equal(1, order.LineCount);
        Assert.Equal(4, result.Skipped.Count);
        Assert.Contains(result.Skipped, s => s.Sku == "NOSUP" && s.Reason.Contains("preferred supplier"));
        Assert.Contains(result.Skipped, s => s.Sku == "ZERO" && s.Reason.Contains("at least 1"));
        Assert.Contains(result.Skipped, s => s.Sku == "OLD" && s.Reason.Contains("archived"));
        Assert.Contains(result.Skipped, s => s.ProductId == 99999);
    }

    [Fact]
    public async Task A_part_with_an_archived_preferred_supplier_is_skipped_with_the_reason()
    {
        var supplier = await NewSupplierAsync("Gone");
        var part = await LowPartAsync("ARCH", supplier);
        await using (var context = Db.CreateContext())
        {
            await context.Suppliers.Where(s => s.Id == supplier).ExecuteUpdateAsync(u => u.SetProperty(s => s.IsActive, false));
        }

        var result = await WithReorderAsync(s => s.CreateDraftPosAsync([new(part, 3)], Actor));

        Assert.Empty(result.Created);
        Assert.Contains("archived", Assert.Single(result.Skipped).Reason);
    }

    [Fact]
    public async Task A_doubled_product_is_ordered_once()
    {
        var supplier = await NewSupplierAsync();
        var part = await LowPartAsync("TWICE", supplier);

        var result = await WithReorderAsync(s => s.CreateDraftPosAsync([new(part, 3), new(part, 9)], Actor));

        var order = Assert.Single(result.Created);
        Assert.Equal(3L * 100, order.TotalCents);
    }

    [Fact]
    public async Task A_supplier_whose_draft_cannot_be_created_has_its_items_skipped_and_the_others_still_get_theirs()
    {
        var acme = await NewSupplierAsync("Acme");
        var bolt = await NewSupplierAsync("Bolt Co");
        var tooMany = await LowPartAsync("A-1", acme);
        var fine = await LowPartAsync("B-1", bolt);

        var result = await WithReorderAsync(s => s.CreateDraftPosAsync(
            [new(tooMany, PurchaseOrderService.MaxQuantity + 1), new(fine, 2)], Actor));

        Assert.Equal("Bolt Co", Assert.Single(result.Created).SupplierName);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("A-1", skipped.Sku);
        Assert.Contains("Acme", skipped.Reason);
    }

    [Fact]
    public async Task Nothing_requested_creates_nothing()
    {
        var result = await WithReorderAsync(s => s.CreateDraftPosAsync([], Actor));

        Assert.Empty(result.Created);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public async Task Too_many_items_in_one_call_are_refused()
    {
        var requests = Enumerable.Range(1, ReorderService.MaxRequests + 1).Select(i => new ReorderRequest(i, 1)).ToList();

        await Assert.ThrowsAsync<ArgumentException>(() => WithReorderAsync(s => s.CreateDraftPosAsync(requests, Actor)));
    }
}
