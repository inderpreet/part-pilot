using InventoryTracker.Services;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-10-2: the BOM status check against real stock, orders and drafts (F10 FR-4, FR-5).</summary>
public sealed class ProjectStatusTests : ProjectTestBase
{
    private async Task<(int ProjectId, int PartId)> OneLineAsync(int perUnit, int target, int onHand)
    {
        var project = await NewProjectAsync("Robot", target);
        var part = await PartAsync("A", onHand, costCents: 100);
        await AddPartAsync(project.Id, part, perUnit);
        return (project.Id, part);
    }

    [Fact]
    public async Task Stock_that_covers_the_need_is_ready()
    {
        var (projectId, _) = await OneLineAsync(perUnit: 2, target: 5, onHand: 10);

        var status = await StatusAsync(projectId);

        var line = Assert.Single(status.Lines);
        Assert.Equal(10, line.Needed);
        Assert.Equal(10, line.OnHand);
        Assert.Equal(0, line.Short);
        Assert.Equal(LineState.Ready, line.State);
        Assert.Equal(ProjectState.Ready, status.State);
    }

    [Fact]
    public async Task A_submitted_order_that_covers_the_rest_makes_the_line_covered()
    {
        var (projectId, part) = await OneLineAsync(2, 5, onHand: 4);
        await OpenOrderAsync(await NewSupplierAsync(), (part, 6));

        var line = Assert.Single((await StatusAsync(projectId)).Lines);

        Assert.Equal(6, line.OnOrder);
        Assert.Equal(6, line.Short);
        Assert.Equal(0, line.ToOrder);
        Assert.Equal(LineState.Covered, line.State);
    }

    [Fact]
    public async Task What_a_partly_received_order_has_already_delivered_is_not_counted_twice()
    {
        var (projectId, part) = await OneLineAsync(1, 10, onHand: 0);
        var order = await OpenOrderAsync(await NewSupplierAsync(), (part, 10));
        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 4)])).Succeeded);

        var line = Assert.Single((await StatusAsync(projectId)).Lines);

        Assert.Equal(4, line.OnHand);
        Assert.Equal(6, line.OnOrder);
        Assert.Equal(LineState.Covered, line.State);
    }

    [Fact]
    public async Task A_draft_that_covers_the_need_makes_the_line_in_draft_and_names_the_orders()
    {
        var (projectId, part) = await OneLineAsync(1, 10, onHand: 0);
        var draft = await DraftWithAsync(await NewSupplierAsync(), (part, 10));

        var line = Assert.Single((await StatusAsync(projectId)).Lines);

        Assert.Equal(LineState.InDraft, line.State);
        Assert.Equal(10, line.InDraft);
        var entry = Assert.Single(line.DraftOrders);
        Assert.Equal(draft.Id, entry.PurchaseOrderId);
        Assert.Equal(draft.Number, entry.Number);
        Assert.Equal(0, line.OnOrder);
        Assert.Equal(10, line.ToOrder); // a draft is an intention: it never reduces what is left to order
    }

    [Fact]
    public async Task A_draft_that_does_not_cover_the_need_leaves_it_short_but_reports_the_quantity()
    {
        var (projectId, part) = await OneLineAsync(1, 10, onHand: 0);
        await DraftWithAsync(await NewSupplierAsync(), (part, 3));

        var line = Assert.Single((await StatusAsync(projectId)).Lines);

        Assert.Equal(LineState.Short, line.State);
        Assert.Equal(3, line.InDraft);
        Assert.Equal(10, line.ToOrder);
        Assert.Equal(7, line.UncoveredByDrafts);
    }

    [Fact]
    public async Task Drafts_across_suppliers_add_up_and_both_are_listed()
    {
        var (projectId, part) = await OneLineAsync(1, 10, onHand: 0);
        await DraftWithAsync(await NewSupplierAsync("Acme"), (part, 4));
        await DraftWithAsync(await NewSupplierAsync("Bolt"), (part, 6));

        var line = Assert.Single((await StatusAsync(projectId)).Lines);

        Assert.Equal(10, line.InDraft);
        Assert.Equal(2, line.DraftOrders.Count);
        Assert.Equal(LineState.InDraft, line.State);
    }

    [Fact]
    public async Task A_draft_never_turns_a_line_ready_or_covered()
    {
        var (readyProject, readyPart) = await OneLineAsync(1, 3, onHand: 3);
        await DraftWithAsync(await NewSupplierAsync(), (readyPart, 50));

        Assert.Equal(LineState.Ready, Assert.Single((await StatusAsync(readyProject)).Lines).State);
    }

    [Fact]
    public async Task Deleting_the_draft_returns_the_line_to_short()
    {
        var (projectId, part) = await OneLineAsync(1, 10, onHand: 0);
        var draft = await DraftWithAsync(await NewSupplierAsync(), (part, 10));
        Assert.Equal(LineState.InDraft, Assert.Single((await StatusAsync(projectId)).Lines).State);

        Assert.True((await RunAsync(o => o.DeleteDraftAsync(draft.Id, Actor))).Succeeded);

        var line = Assert.Single((await StatusAsync(projectId)).Lines);
        Assert.Equal(LineState.Short, line.State);
        Assert.Empty(line.DraftOrders);
    }

    [Fact]
    public async Task Submitting_the_draft_moves_it_from_draft_to_on_order()
    {
        var (projectId, part) = await OneLineAsync(1, 10, onHand: 0);
        var draft = await DraftWithAsync(await NewSupplierAsync(), (part, 10));

        Assert.True((await RunAsync(async o => (await o.SubmitAsync(draft.Id, Actor)).Succeeded ? Result.Success() : Result.Failure("x", "x"))).Succeeded);

        var line = Assert.Single((await StatusAsync(projectId)).Lines);
        Assert.Equal(0, line.InDraft);
        Assert.Equal(10, line.OnOrder);
        Assert.Equal(LineState.Covered, line.State);
    }

    [Fact]
    public async Task The_project_state_is_its_worst_line()
    {
        var project = await NewProjectAsync("Robot");
        var ready = await PartAsync("READY", onHand: 5);
        var covered = await PartAsync("COVERED", onHand: 0);
        var inDraft = await PartAsync("DRAFT", onHand: 0);
        var short1 = await PartAsync("SHORT", onHand: 0);
        foreach (var part in new[] { ready, covered, inDraft, short1 })
        {
            await AddPartAsync(project.Id, part, 2);
        }

        var supplier = await NewSupplierAsync();
        await OpenOrderAsync(supplier, (covered, 2));
        await DraftWithAsync(supplier, (inDraft, 2));

        var status = await StatusAsync(project.Id);

        Assert.Equal(ProjectState.Short, status.State);
        Assert.Equal([LineState.Ready, LineState.Covered, LineState.InDraft, LineState.Short], status.Lines.Select(l => l.State));
        var summary = status.Summary;
        Assert.Equal((1, 1, 1, 1, 4), (summary.Ready, summary.Covered, summary.InDraft, summary.Short, summary.LineCount));
    }

    [Fact]
    public async Task Without_a_short_line_the_worst_remaining_state_wins()
    {
        var project = await NewProjectAsync("Robot");
        var ready = await PartAsync("READY", onHand: 5);
        var inDraft = await PartAsync("DRAFT", onHand: 0);
        await AddPartAsync(project.Id, ready, 1);
        await AddPartAsync(project.Id, inDraft, 1);
        await DraftWithAsync(await NewSupplierAsync(), (inDraft, 1));

        Assert.Equal(ProjectState.InDraft, (await StatusAsync(project.Id)).State);
    }

    [Fact]
    public async Task An_empty_project_is_empty()
    {
        var project = await NewProjectAsync("Robot");

        var status = await StatusAsync(project.Id);

        Assert.Equal(ProjectState.Empty, status.State);
        Assert.Empty(status.Lines);
    }

    [Fact]
    public async Task Units_remaining_after_a_partial_build_shrink_what_is_needed()
    {
        var (projectId, part) = await OneLineAsync(perUnit: 2, target: 5, onHand: 10);
        Assert.True((await WithProjectsAsync(p => p.BuildAsync(projectId, 2, null, Who))).Succeeded);

        var status = await StatusAsync(projectId);

        Assert.Equal(2, status.BuiltUnits);
        Assert.Equal(3, status.RemainingUnits);
        var line = Assert.Single(status.Lines);
        Assert.Equal(6, line.Needed);
        Assert.Equal(6, line.OnHand); // 10 less the 4 the build issued
        Assert.Equal(LineState.Ready, line.State);
        Assert.Equal(6, await OnHandAsync(part));
    }

    [Fact]
    public async Task A_fully_built_project_is_built_and_needs_nothing()
    {
        var (projectId, _) = await OneLineAsync(perUnit: 2, target: 3, onHand: 6);
        Assert.True((await WithProjectsAsync(p => p.BuildAsync(projectId, 3, null, Who))).Succeeded);

        var status = await StatusAsync(projectId);

        Assert.Equal(ProjectState.Built, status.State);
        Assert.Equal(0, status.RemainingUnits);
        Assert.Equal(0, Assert.Single(status.Lines).Needed);
        Assert.Empty(status.Shortages);
    }

    [Fact]
    public async Task Building_more_than_the_target_leaves_none_remaining()
    {
        var (projectId, _) = await OneLineAsync(perUnit: 1, target: 2, onHand: 10);
        Assert.True((await WithProjectsAsync(p => p.BuildAsync(projectId, 4, null, Who))).Succeeded);

        Assert.Equal(0, (await StatusAsync(projectId)).RemainingUnits);
    }

    [Fact]
    public async Task An_archived_part_is_flagged()
    {
        var (projectId, part) = await OneLineAsync(1, 1, onHand: 1);
        await ArchiveAsync(part);

        var line = Assert.Single((await StatusAsync(projectId)).Lines);

        Assert.False(line.ProductIsActive);
    }

    [Fact]
    public async Task Cost_per_unit_and_for_the_remaining_units_are_calculated_from_the_part_costs()
    {
        var project = await NewProjectAsync("Robot", target: 4);
        await AddPartAsync(project.Id, await PartAsync("A", costCents: 250), 3);
        await AddPartAsync(project.Id, await PartAsync("B", costCents: 1_000), 1);

        var status = await StatusAsync(project.Id);

        Assert.Equal(1_750, status.CostPerUnitCents);
        Assert.Equal(7_000, status.RemainingCostCents);
        Assert.Equal(1_750, (await DetailAsync(project.Id)).BomCostCents);
    }

    [Fact]
    public async Task Stock_is_not_reserved_so_two_projects_can_need_the_same_stock()
    {
        var part = await PartAsync("A", onHand: 5);
        var first = await NewProjectAsync("Robot");
        var second = await NewProjectAsync("Drone");
        await AddPartAsync(first.Id, part, 5);
        await AddPartAsync(second.Id, part, 5);

        Assert.Equal(LineState.Ready, Assert.Single((await StatusAsync(first.Id)).Lines).State);
        Assert.Equal(LineState.Ready, Assert.Single((await StatusAsync(second.Id)).Lines).State);
    }

    [Fact]
    public async Task A_missing_project_has_no_status()
    {
        Assert.Null(await WithProjectsAsync(p => p.GetStatusAsync(9_999)));
        Assert.Empty(await WithProjectsAsync(p => p.GetStatusSummariesAsync([])));
    }

    [Fact]
    public async Task Summaries_are_returned_per_project()
    {
        var shortProject = await NewProjectAsync("Short");
        var readyProject = await NewProjectAsync("Ready");
        await AddPartAsync(shortProject.Id, await PartAsync("A"), 1);
        await AddPartAsync(readyProject.Id, await PartAsync("B", onHand: 1), 1);

        var summaries = await WithProjectsAsync(p => p.GetStatusSummariesAsync([shortProject.Id, readyProject.Id]));

        Assert.Equal(ProjectState.Short, summaries[shortProject.Id].State);
        Assert.Equal(ProjectState.Ready, summaries[readyProject.Id].State);
    }
}
