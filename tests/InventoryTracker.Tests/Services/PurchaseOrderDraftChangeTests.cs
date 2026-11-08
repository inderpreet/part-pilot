using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-10-2: the draft-only queries and changes the BOM shortage review relies on.</summary>
public sealed class PurchaseOrderDraftChangeTests : ProjectTestBase
{
    private async Task<IReadOnlyDictionary<int, InDraftQuantity>> InDraftAsync(params int[] productIds)
        => await QueryAsync(o => o.GetInDraftByProductAsync(productIds.Length == 0 ? null : productIds));

    private async Task<Result<IReadOnlyList<DraftChangeOutcome>>> ApplyAsync(params DraftLineChange[] changes)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            return await orders.ApplyDraftChangesAsync(changes);
        }
    }

    // ---- GetInDraftByProductAsync ----------------------------------------------------------------------------------

    [Fact]
    public async Task In_draft_sums_across_drafts_and_suppliers_and_lists_each_order()
    {
        var part = await NewProductAsync("P1");
        var acme = await NewSupplierAsync("Acme");
        var bolt = await NewSupplierAsync("Bolt");
        var first = await DraftAsync(acme, null, new PurchaseOrderLineInput(part, 4));
        var second = await DraftAsync(bolt, null, new PurchaseOrderLineInput(part, 6));

        var result = await InDraftAsync(part);

        var entry = Assert.Single(result).Value;
        Assert.Equal(10, entry.Total);
        Assert.Equal(2, entry.Orders.Count);
        Assert.Contains(entry.Orders, o => o.PurchaseOrderId == first.Id && o.Number == first.Number && o.Quantity == 4);
        Assert.Contains(entry.Orders, o => o.PurchaseOrderId == second.Id && o.Number == second.Number && o.Quantity == 6);
    }

    [Fact]
    public async Task In_draft_ignores_submitted_received_and_cancelled_orders()
    {
        var part = await NewProductAsync("P1");
        var supplier = await NewSupplierAsync();
        await OpenOrderAsync(supplier, (part, 5));
        var cancelled = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 7));
        Assert.True((await RunAsync(o => o.CancelAsync(cancelled.Id, Actor))).Succeeded);
        var received = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 3));
        Assert.True((await ReceiveAsync(received.Id, [new ReceiveLineInput(received.Lines[0].Id, 3)])).Succeeded);

        Assert.Empty(await InDraftAsync());
    }

    [Fact]
    public async Task In_draft_has_no_row_for_a_part_that_is_in_no_draft_and_can_be_limited_to_some_parts()
    {
        var inDraft = await NewProductAsync("P1");
        var other = await NewProductAsync("P2");
        var third = await NewProductAsync("P3");
        var supplier = await NewSupplierAsync();
        await DraftAsync(supplier, null, new PurchaseOrderLineInput(inDraft, 2), new PurchaseOrderLineInput(other, 9));

        var limited = await InDraftAsync(inDraft, third);

        Assert.Equal([inDraft], limited.Keys.ToArray());
        Assert.Empty(await QueryAsync(o => o.GetInDraftByProductAsync([])));
        Assert.Equal(2, (await InDraftAsync()).Count);
    }

    [Fact]
    public async Task Deleting_the_draft_removes_it_from_in_draft()
    {
        var part = await NewProductAsync("P1");
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(part, 2));

        Assert.True((await RunAsync(o => o.DeleteDraftAsync(draft.Id, Actor))).Succeeded);

        Assert.Empty(await InDraftAsync(part));
    }

    // ---- ApplyDraftChangesAsync -----------------------------------------------------------------------------------

    [Fact]
    public async Task An_add_puts_the_part_on_the_draft_at_the_supplier_price_else_the_product_cost()
    {
        var linked = await NewProductAsync("P1");
        var unlinked = (await CreateProductAsync(Input(sku: "P2", name: "Plain", costCents: 250))).Id;
        var supplier = await NewSupplierAsync();
        await LinkAsync(linked, supplier, costCents: 1_200);
        var draft = await DraftAsync(supplier);

        var result = await ApplyAsync(
            new DraftLineChange(draft.Id, linked, DraftChangeKind.Add, 5),
            new DraftLineChange(draft.Id, unlinked, DraftChangeKind.Add, 3));

        Assert.True(result.Succeeded, Messages(result));
        Assert.All(result.Value, o => Assert.Equal(DraftChangeStatus.Added, o.Status));
        var after = await GetOrderAsync(draft.Id);
        Assert.Equal(1_200, after.Lines.Single(l => l.ProductId == linked).UnitCostCents);
        Assert.Equal(250, after.Lines.Single(l => l.ProductId == unlinked).UnitCostCents);
        Assert.Equal(5, after.Lines.Single(l => l.ProductId == linked).QuantityOrdered);
    }

    [Fact]
    public async Task Setting_a_quantity_changes_the_line_and_keeps_its_cost()
    {
        var part = await NewProductAsync("P1");
        var supplier = await NewSupplierAsync();
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 4, 777));

        var result = await ApplyAsync(new DraftLineChange(draft.Id, part, DraftChangeKind.SetQuantity, 9));

        Assert.True(result.Succeeded, Messages(result));
        var outcome = Assert.Single(result.Value);
        Assert.Equal(DraftChangeStatus.Updated, outcome.Status);
        Assert.Equal(4, outcome.PreviousQuantity);
        var line = Assert.Single((await GetOrderAsync(draft.Id)).Lines);
        Assert.Equal(9, line.QuantityOrdered);
        Assert.Equal(777, line.UnitCostCents);
    }

    [Fact]
    public async Task Changes_for_several_drafts_apply_together_and_report_in_order()
    {
        var a = await NewProductAsync("A");
        var b = await NewProductAsync("B");
        var supplier = await NewSupplierAsync();
        var first = await DraftAsync(supplier, null, new PurchaseOrderLineInput(a, 1));
        var second = await DraftAsync(supplier, null, new PurchaseOrderLineInput(b, 2));

        var result = await ApplyAsync(
            new DraftLineChange(second.Id, b, DraftChangeKind.SetQuantity, 20),
            new DraftLineChange(first.Id, b, DraftChangeKind.Add, 6),
            new DraftLineChange(first.Id, a, DraftChangeKind.SetQuantity, 11));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal([DraftChangeStatus.Updated, DraftChangeStatus.Added, DraftChangeStatus.Updated], result.Value.Select(o => o.Status));
        Assert.Equal(20, (await GetOrderAsync(second.Id)).Lines.Single().QuantityOrdered);
        Assert.Equal([11, 6], (await GetOrderAsync(first.Id)).Lines.OrderBy(l => l.Id).Select(l => l.QuantityOrdered));
    }

    [Fact]
    public async Task An_add_for_a_part_already_on_that_order_is_skipped_with_a_reason()
    {
        var part = await NewProductAsync("P1");
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(part, 4));

        var result = await ApplyAsync(new DraftLineChange(draft.Id, part, DraftChangeKind.Add, 9));

        Assert.True(result.Succeeded);
        var outcome = Assert.Single(result.Value);
        Assert.Equal(DraftChangeStatus.Skipped, outcome.Status);
        Assert.Contains("already on", outcome.Reason);
        Assert.Equal(4, Assert.Single((await GetOrderAsync(draft.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task The_same_part_added_twice_in_one_call_is_added_once()
    {
        var part = await NewProductAsync("P1");
        var draft = await DraftAsync(await NewSupplierAsync());

        var result = await ApplyAsync(
            new DraftLineChange(draft.Id, part, DraftChangeKind.Add, 2),
            new DraftLineChange(draft.Id, part, DraftChangeKind.Add, 3));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal([DraftChangeStatus.Added, DraftChangeStatus.Skipped], result.Value.Select(o => o.Status));
        Assert.Equal(2, Assert.Single((await GetOrderAsync(draft.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task An_archived_or_missing_part_is_skipped_and_the_rest_still_applied()
    {
        var good = await NewProductAsync("GOOD");
        var archived = await NewProductAsync("OLD", archived: true);
        var draft = await DraftAsync(await NewSupplierAsync());

        var result = await ApplyAsync(
            new DraftLineChange(draft.Id, archived, DraftChangeKind.Add, 2),
            new DraftLineChange(draft.Id, 99_999, DraftChangeKind.Add, 2),
            new DraftLineChange(draft.Id, good, DraftChangeKind.Add, 2));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal([DraftChangeStatus.Skipped, DraftChangeStatus.Skipped, DraftChangeStatus.Added], result.Value.Select(o => o.Status));
        Assert.Contains("archived", result.Value[0].Reason);
        Assert.Equal(good, Assert.Single((await GetOrderAsync(draft.Id)).Lines).ProductId);
    }

    [Fact]
    public async Task An_add_with_a_quantity_below_one_is_skipped()
    {
        var part = await NewProductAsync("P1");
        var draft = await DraftAsync(await NewSupplierAsync());

        var result = await ApplyAsync(new DraftLineChange(draft.Id, part, DraftChangeKind.Add, 0));

        Assert.True(result.Succeeded);
        Assert.Equal(DraftChangeStatus.Skipped, Assert.Single(result.Value).Status);
        Assert.Empty((await GetOrderAsync(draft.Id)).Lines);
    }

    [Fact]
    public async Task A_quantity_change_below_one_is_refused()
    {
        var part = await NewProductAsync("P1");
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(part, 4));

        var result = await ApplyAsync(new DraftLineChange(draft.Id, part, DraftChangeKind.SetQuantity, 0));

        AssertFailed(result, "po.quantity_positive");
        Assert.Equal(4, (await GetOrderAsync(draft.Id)).Lines.Single().QuantityOrdered);
    }

    [Fact]
    public async Task A_quantity_change_for_a_part_not_on_the_draft_refuses_everything()
    {
        var onIt = await NewProductAsync("P1");
        var notOnIt = await NewProductAsync("P2");
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(onIt, 4));

        var result = await ApplyAsync(
            new DraftLineChange(draft.Id, onIt, DraftChangeKind.SetQuantity, 8),
            new DraftLineChange(draft.Id, notOnIt, DraftChangeKind.SetQuantity, 8));

        AssertFailed(result, "po.line_not_found");
        Assert.Equal(4, (await GetOrderAsync(draft.Id)).Lines.Single().QuantityOrdered);
    }

    [Fact]
    public async Task Anything_that_is_not_a_draft_refuses_everything_and_applies_nothing()
    {
        var part = await NewProductAsync("P1");
        var other = await NewProductAsync("P2");
        var supplier = await NewSupplierAsync();
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 4));
        var submitted = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 5));

        var result = await ApplyAsync(
            new DraftLineChange(draft.Id, part, DraftChangeKind.SetQuantity, 40),
            new DraftLineChange(submitted.Id, other, DraftChangeKind.Add, 1));

        AssertFailed(result, "po.not_editable");
        Assert.Equal(4, (await GetOrderAsync(draft.Id)).Lines.Single().QuantityOrdered);
        Assert.Single((await GetOrderAsync(submitted.Id)).Lines);
    }

    [Fact]
    public async Task A_missing_order_refuses_the_call()
    {
        var part = await NewProductAsync("P1");

        var result = await ApplyAsync(new DraftLineChange(424_242, part, DraftChangeKind.Add, 1));

        AssertFailed(result, "po.not_found");
    }

    [Fact]
    public async Task No_changes_is_a_successful_no_op()
    {
        var result = await ApplyAsync();

        Assert.True(result.Succeeded);
        Assert.Empty(result.Value);
    }

    [Fact]
    public async Task A_draft_submitted_meanwhile_is_not_touched_by_a_later_call()
    {
        var part = await NewProductAsync("P1");
        var supplier = await NewSupplierAsync();
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 4));
        Assert.True((await RunAsync(async o => (await o.SubmitAsync(draft.Id, Actor)).Succeeded ? Result.Success() : Result.Failure("x", "x"))).Succeeded);

        var result = await ApplyAsync(new DraftLineChange(draft.Id, part, DraftChangeKind.SetQuantity, 99));

        AssertFailed(result, "po.not_editable");
        await using var context = Db.CreateContext();
        Assert.Equal(4, await context.PurchaseOrderLines.Where(l => l.PurchaseOrderId == draft.Id).Select(l => l.QuantityOrdered).SingleAsync());
    }
}
