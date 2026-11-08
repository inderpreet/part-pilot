using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-10-2: ordering a project's shortages onto Draft purchase orders (F10 FR-6).</summary>
public sealed class ProjectShortageTests : ProjectTestBase
{
    private async Task<Result<AddShortagesResult>> OrderAsync(int projectId, ShortageTarget? target, params ShortageDecision[] decisions)
        => await WithProjectsAsync(p => p.AddShortagesToOrderAsync(projectId, decisions, target, Actor));

    private async Task<(int ProjectId, BomLineItem Line, int PartId)> ShortLineAsync(string sku = "A", int perUnit = 5, int onHand = 0)
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync(sku, onHand, costCents: 300);
        var line = await AddPartAsync(project.Id, part, perUnit);
        return (project.Id, line, part);
    }

    private static ShortageDecision Add(BomLineItem line, int quantity) => new(line.Id, ShortageAction.Add, quantity);

    private static ShortageDecision Update(BomLineItem line, int quantity, int purchaseOrderId) => new(line.Id, ShortageAction.UpdateQuantity, quantity, purchaseOrderId);

    private static ShortageDecision Skip(BomLineItem line) => new(line.Id, ShortageAction.Skip, 0);

    [Fact]
    public async Task Add_lands_on_the_chosen_draft_with_the_supplier_price()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var supplier = await NewSupplierAsync();
        await LinkAsync(part, supplier, costCents: 1_250);
        var draft = await DraftAsync(supplier);

        var result = await OrderAsync(projectId, ShortageTarget.ExistingDraft(draft.Id), Add(line, 5));

        Assert.True(result.Succeeded, Messages(result));
        var outcome = Assert.Single(result.Value.Outcomes);
        Assert.Equal(ShortageOutcomeKind.Added, outcome.Kind);
        Assert.Equal(draft.Number, outcome.PurchaseOrderNumber);
        Assert.Null(result.Value.CreatedPurchaseOrderId);
        var placed = Assert.Single((await GetOrderAsync(draft.Id)).Lines);
        Assert.Equal(5, placed.QuantityOrdered);
        Assert.Equal(1_250, placed.UnitCostCents);
    }

    [Fact]
    public async Task Without_a_supplier_price_the_product_cost_is_used()
    {
        var (projectId, line, _) = await ShortLineAsync();
        var draft = await DraftAsync(await NewSupplierAsync());

        await OrderAsync(projectId, ShortageTarget.ExistingDraft(draft.Id), Add(line, 5));

        Assert.Equal(300, Assert.Single((await GetOrderAsync(draft.Id)).Lines).UnitCostCents);
    }

    [Fact]
    public async Task A_new_draft_is_created_and_numbered_for_the_chosen_supplier()
    {
        var (projectId, line, _) = await ShortLineAsync();
        var supplier = await NewSupplierAsync("Acme");

        var result = await OrderAsync(projectId, ShortageTarget.NewDraft(supplier), Add(line, 5));

        Assert.True(result.Succeeded, Messages(result));
        Assert.NotNull(result.Value.CreatedPurchaseOrderId);
        Assert.Equal("PO-2026-0001", result.Value.CreatedPurchaseOrderNumber);
        var created = await GetOrderAsync(result.Value.CreatedPurchaseOrderId!.Value);
        Assert.Equal(PurchaseOrderStatus.Draft, created.Status);
        Assert.Equal(supplier, created.SupplierId);
        Assert.Equal(5, Assert.Single(created.Lines).QuantityOrdered);
    }

    [Fact]
    public async Task No_draft_is_created_when_no_decision_adds()
    {
        var (projectId, line, _) = await ShortLineAsync();
        var supplier = await NewSupplierAsync();

        var result = await OrderAsync(projectId, ShortageTarget.NewDraft(supplier), Skip(line));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Null(result.Value.CreatedPurchaseOrderId);
        await using var context = Db.CreateContext();
        Assert.Empty(await context.PurchaseOrders.ToListAsync());
    }

    [Fact]
    public async Task A_target_is_required_only_when_something_is_added()
    {
        var (projectId, line, _) = await ShortLineAsync();

        AssertFailed(await OrderAsync(projectId, null, Add(line, 5)), "project.target_required", ProjectFields.Target);
        Assert.True((await OrderAsync(projectId, null, Skip(line))).Succeeded);
    }

    [Fact]
    public async Task A_new_draft_for_an_archived_supplier_is_refused_on_the_target_field()
    {
        var (projectId, line, _) = await ShortLineAsync();
        var supplier = await NewSupplierAsync("Gone", archived: true);

        var result = await OrderAsync(projectId, ShortageTarget.NewDraft(supplier), Add(line, 5));

        AssertFailed(result, "po.supplier_archived", ProjectFields.Target);
    }

    [Fact]
    public async Task Update_quantity_sets_the_quantity_on_the_named_draft_line_and_keeps_its_cost()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(part, 2, 990));

        var result = await OrderAsync(projectId, null, Update(line, 5, draft.Id));

        Assert.True(result.Succeeded, Messages(result));
        var outcome = Assert.Single(result.Value.Outcomes);
        Assert.Equal(ShortageOutcomeKind.Updated, outcome.Kind);
        Assert.Equal(2, outcome.PreviousQuantity);
        Assert.Equal(5, outcome.Quantity);
        var after = Assert.Single((await GetOrderAsync(draft.Id)).Lines);
        Assert.Equal(5, after.QuantityOrdered);
        Assert.Equal(990, after.UnitCostCents);
    }

    [Fact]
    public async Task Skip_changes_nothing()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(part, 2));

        var result = await OrderAsync(projectId, ShortageTarget.ExistingDraft(draft.Id), Skip(line));

        Assert.True(result.Succeeded);
        Assert.Equal(ShortageOutcomeKind.Skipped, Assert.Single(result.Value.Outcomes).Kind);
        Assert.Equal(2, Assert.Single((await GetOrderAsync(draft.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task Add_update_and_skip_can_be_mixed_in_one_call()
    {
        var project = await NewProjectAsync("Robot");
        var toAdd = await PartAsync("ADD", costCents: 100);
        var toUpdate = await PartAsync("UPD", costCents: 100);
        var toSkip = await PartAsync("SKIP", costCents: 100);
        var addLine = await AddPartAsync(project.Id, toAdd, 4);
        var updateLine = await AddPartAsync(project.Id, toUpdate, 6);
        var skipLine = await AddPartAsync(project.Id, toSkip, 2);
        var supplier = await NewSupplierAsync();
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(toUpdate, 1));

        var result = await OrderAsync(
            project.Id,
            ShortageTarget.ExistingDraft(draft.Id),
            Add(addLine, 4),
            Update(updateLine, 6, draft.Id),
            Skip(skipLine));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal(
            [ShortageOutcomeKind.Added, ShortageOutcomeKind.Updated, ShortageOutcomeKind.Skipped],
            result.Value.Outcomes.Select(o => o.Kind));
        Assert.Single(result.Value.Added);
        Assert.Single(result.Value.Updated);
        Assert.Single(result.Value.Skipped);
        var lines = (await GetOrderAsync(draft.Id)).Lines.OrderBy(l => l.Id).ToList();
        Assert.Equal([("UPD", 6), ("ADD", 4)], lines.Select(l => (l.Sku, l.QuantityOrdered)));
    }

    [Fact]
    public async Task A_part_held_by_two_drafts_can_be_updated_on_either()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var supplier = await NewSupplierAsync();
        var first = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 1));
        var second = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 2));

        Assert.True((await OrderAsync(projectId, null, Update(line, 9, second.Id))).Succeeded);

        Assert.Equal(1, (await GetOrderAsync(first.Id)).Lines.Single().QuantityOrdered);
        Assert.Equal(9, (await GetOrderAsync(second.Id)).Lines.Single().QuantityOrdered);
    }

    [Fact]
    public async Task An_add_for_a_part_the_target_already_holds_is_skipped_with_a_reason()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(part, 2));

        var result = await OrderAsync(projectId, ShortageTarget.ExistingDraft(draft.Id), Add(line, 5));

        Assert.True(result.Succeeded);
        var outcome = Assert.Single(result.Value.Outcomes);
        Assert.Equal(ShortageOutcomeKind.Skipped, outcome.Kind);
        Assert.Contains("already on", outcome.Reason);
        Assert.Equal(2, Assert.Single((await GetOrderAsync(draft.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task An_archived_part_is_skipped_with_a_reason_and_the_rest_applied()
    {
        var project = await NewProjectAsync("Robot");
        var old = await PartAsync("OLD");
        var fine = await PartAsync("FINE");
        var oldLine = await AddPartAsync(project.Id, old, 2);
        var fineLine = await AddPartAsync(project.Id, fine, 2);
        await ArchiveAsync(old);
        var draft = await DraftAsync(await NewSupplierAsync());

        var result = await OrderAsync(project.Id, ShortageTarget.ExistingDraft(draft.Id), Add(oldLine, 2), Add(fineLine, 2));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal([ShortageOutcomeKind.Skipped, ShortageOutcomeKind.Added], result.Value.Outcomes.Select(o => o.Kind));
        Assert.Contains("archived", result.Value.Outcomes[0].Reason);
        Assert.Equal("FINE", Assert.Single((await GetOrderAsync(draft.Id)).Lines).Sku);
    }

    [Fact]
    public async Task Update_for_a_part_that_is_not_on_that_draft_is_refused_and_nothing_is_applied()
    {
        var project = await NewProjectAsync("Robot");
        var first = await PartAsync("A");
        var second = await PartAsync("B");
        var firstLine = await AddPartAsync(project.Id, first, 2);
        var secondLine = await AddPartAsync(project.Id, second, 2);
        var draft = await DraftAsync(await NewSupplierAsync(), null, new PurchaseOrderLineInput(first, 1));

        var result = await OrderAsync(project.Id, ShortageTarget.ExistingDraft(draft.Id), Update(firstLine, 8, draft.Id), Update(secondLine, 8, draft.Id));

        AssertFailed(result, "po.line_not_found");
        Assert.Equal(1, Assert.Single((await GetOrderAsync(draft.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task A_target_that_is_no_longer_a_draft_refuses_everything()
    {
        var project = await NewProjectAsync("Robot");
        var first = await PartAsync("A");
        var second = await PartAsync("B");
        var firstLine = await AddPartAsync(project.Id, first, 2);
        var secondLine = await AddPartAsync(project.Id, second, 2);
        var supplier = await NewSupplierAsync();
        var openDraft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(first, 1));
        var submitted = await SubmittedAsync(supplier, new PurchaseOrderLineInput(second, 1));

        var result = await OrderAsync(project.Id, ShortageTarget.ExistingDraft(openDraft.Id), Update(firstLine, 8, openDraft.Id), Update(secondLine, 8, submitted.Id));

        AssertFailed(result, "po.not_editable");
        Assert.Equal(1, Assert.Single((await GetOrderAsync(openDraft.Id)).Lines).QuantityOrdered);
        Assert.Equal(1, Assert.Single((await GetOrderAsync(submitted.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task A_new_draft_is_not_left_behind_when_the_call_is_refused()
    {
        var project = await NewProjectAsync("Robot");
        var first = await PartAsync("A");
        var second = await PartAsync("B");
        var firstLine = await AddPartAsync(project.Id, first, 2);
        var secondLine = await AddPartAsync(project.Id, second, 2);
        var supplier = await NewSupplierAsync();
        var submitted = await SubmittedAsync(supplier, new PurchaseOrderLineInput(second, 1));
        await using (var before = Db.CreateContext())
        {
            Assert.Equal(1, await before.PurchaseOrders.CountAsync());
        }

        var result = await OrderAsync(project.Id, ShortageTarget.NewDraft(supplier), Add(firstLine, 2), Update(secondLine, 3, submitted.Id));

        AssertFailed(result, "po.not_editable");
        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.PurchaseOrders.CountAsync());
    }

    [Fact]
    public async Task Quantities_are_raised_to_the_suppliers_minimum_order_quantity()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var supplier = await NewSupplierAsync();
        await LinkAsync(part, supplier, costCents: 100, minOrder: 25);

        var result = await OrderAsync(projectId, ShortageTarget.NewDraft(supplier), Add(line, 5));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal(25, Assert.Single(result.Value.Outcomes).Quantity);
        Assert.Equal(25, Assert.Single((await GetOrderAsync(result.Value.CreatedPurchaseOrderId!.Value)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task The_minimum_also_applies_when_updating_a_draft_line()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var supplier = await NewSupplierAsync();
        await LinkAsync(part, supplier, costCents: 100, minOrder: 10);
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(part, 20));

        await OrderAsync(projectId, null, Update(line, 4, draft.Id));

        Assert.Equal(10, Assert.Single((await GetOrderAsync(draft.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task A_quantity_above_the_minimum_is_left_alone()
    {
        var (projectId, line, part) = await ShortLineAsync();
        var supplier = await NewSupplierAsync();
        await LinkAsync(part, supplier, costCents: 100, minOrder: 10);
        var draft = await DraftAsync(supplier);

        await OrderAsync(projectId, ShortageTarget.ExistingDraft(draft.Id), Add(line, 37));

        Assert.Equal(37, Assert.Single((await GetOrderAsync(draft.Id)).Lines).QuantityOrdered);
    }

    [Fact]
    public async Task A_decision_for_a_line_of_another_project_is_refused()
    {
        var (_, line, _) = await ShortLineAsync("A");
        var other = await NewProjectAsync("Other");
        var draft = await DraftAsync(await NewSupplierAsync());

        var result = await OrderAsync(other.Id, ShortageTarget.ExistingDraft(draft.Id), Add(line, 2));

        AssertFailed(result, "project.line_missing");
        Assert.Empty((await GetOrderAsync(draft.Id)).Lines);
    }

    [Fact]
    public async Task Bad_decisions_are_refused_before_anything_happens()
    {
        var (projectId, line, _) = await ShortLineAsync();
        var draft = await DraftAsync(await NewSupplierAsync());
        var target = ShortageTarget.ExistingDraft(draft.Id);

        AssertFailed(await OrderAsync(projectId, target, Add(line, 0)), "project.shortage_quantity");
        AssertFailed(await OrderAsync(projectId, target, new ShortageDecision(line.Id, ShortageAction.UpdateQuantity, 3, null)), "project.shortage_draft_required");
        AssertFailed(await OrderAsync(projectId, target, Add(line, 1), Add(line, 2)), "project.shortage_duplicate");
        AssertFailed(await OrderAsync(projectId, new ShortageTarget(null, null), Add(line, 1)), "project.target_required");
        AssertFailed(await OrderAsync(9_999, target, Add(line, 1)), "project.not_found");
        Assert.Empty((await GetOrderAsync(draft.Id)).Lines);
    }

    [Fact]
    public async Task The_status_follows_what_was_ordered()
    {
        var (projectId, line, _) = await ShortLineAsync(perUnit: 5);
        var draft = await DraftAsync(await NewSupplierAsync());
        Assert.Equal(LineState.Short, Assert.Single((await StatusAsync(projectId)).Lines).State);

        await OrderAsync(projectId, ShortageTarget.ExistingDraft(draft.Id), Add(line, 5));

        Assert.Equal(LineState.InDraft, Assert.Single((await StatusAsync(projectId)).Lines).State);
    }
}
