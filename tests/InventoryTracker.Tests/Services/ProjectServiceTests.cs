using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-10-2: projects, numbering and the BOM (F10 FR-1, FR-2, FR-3).</summary>
public sealed class ProjectServiceTests : ProjectTestBase
{
    // ---- create / numbering ------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_first_project_of_a_year_gets_number_0001_and_the_next_0002()
    {
        var first = await NewProjectAsync("Robot");
        var second = await NewProjectAsync("Drone");

        Assert.Equal("PRJ-2026-0001", first.Number);
        Assert.Equal("PRJ-2026-0002", second.Number);
    }

    [Fact]
    public async Task A_new_year_restarts_the_sequence()
    {
        await NewProjectAsync("Robot");
        Clock.UtcNow = new DateTime(2027, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        var next = await NewProjectAsync("Drone");

        Assert.Equal("PRJ-2027-0001", next.Number);
    }

    [Fact]
    public async Task The_prefix_setting_is_honoured()
    {
        Settings.ProjectPrefix = "JOB/";

        var project = await NewProjectAsync("Robot");

        Assert.Equal("JOB/2026-0001", project.Number);
    }

    [Fact]
    public async Task A_number_handed_out_by_a_failed_create_is_handed_back()
    {
        // The second create fails on the name after the number could have been taken; the next one still gets 0002.
        await NewProjectAsync("Robot");
        var duplicate = await WithProjectsAsync(p => p.CreateAsync(ProjectInputOf("robot"), Staff));
        Assert.False(duplicate.Succeeded);

        var next = await NewProjectAsync("Drone");

        Assert.Equal("PRJ-2026-0002", next.Number);
    }

    [Fact]
    public async Task A_create_that_rolls_back_hands_its_number_back()
    {
        var (context, projects, _) = NewProjects();
        await using (context)
        {
            await using var outer = await context.Database.BeginTransactionAsync();
            Assert.Equal("PRJ-2026-0001", await projects.NextNumberAsync(2026));
            // Not committed: disposing the transaction rolls the counter back.
        }

        Assert.Equal("PRJ-2026-0001", await WithProjectsAsync(p => p.NextNumberAsync(2026)));
    }

    [Fact]
    public async Task A_typed_number_is_kept_and_does_not_use_the_sequence()
    {
        var typed = await NewProjectAsync("Robot", number: "ACME-77");
        var generated = await NewProjectAsync("Drone");

        Assert.Equal("ACME-77", typed.Number);
        Assert.Equal("PRJ-2026-0001", generated.Number);
    }

    [Fact]
    public async Task A_generated_number_skips_one_that_someone_typed()
    {
        await NewProjectAsync("Robot", number: "PRJ-2026-0001");

        var generated = await NewProjectAsync("Drone");

        Assert.Equal("PRJ-2026-0002", generated.Number);
    }

    [Fact]
    public async Task A_duplicate_number_in_another_case_is_a_field_error_on_number()
    {
        await NewProjectAsync("Robot", number: "ABC-1");

        var result = await WithProjectsAsync(p => p.CreateAsync(ProjectInputOf("Drone", number: "abc-1"), Staff));

        AssertFailed(result, "project.number_taken", ProjectFields.Number);
    }

    [Fact]
    public async Task A_number_that_is_too_long_is_refused()
    {
        var result = await WithProjectsAsync(p => p.CreateAsync(ProjectInputOf("Robot", number: new string('9', Project.NumberMaxLength + 1)), Staff));

        AssertFailed(result, "project.number_too_long", ProjectFields.Number);
    }

    [Fact]
    public async Task Two_creates_at_the_same_moment_get_different_numbers()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => WithProjectsAsync(p => p.CreateAsync(ProjectInputOf("Job " + i), Staff))));

        Assert.All(results, r => Assert.True(r.Succeeded, Messages(r)));
        Assert.Equal(6, results.Select(r => r.Value.Number).Distinct().Count());
    }

    [Fact]
    public async Task Create_validates_the_fields_and_names_them()
    {
        var result = await WithProjectsAsync(p => p.CreateAsync(
            new ProjectInput(" ", null, new string('d', Project.DescriptionMaxLength + 1), 0, new string('n', Project.NotesMaxLength + 1)), Staff));

        Assert.False(result.Succeeded);
        Assert.Equivalent(
            new[] { ProjectFields.Name, ProjectFields.Description, ProjectFields.TargetUnits, ProjectFields.Notes },
            result.Errors.Select(e => e.Field).ToArray());
    }

    [Fact]
    public async Task Target_units_have_an_upper_limit()
    {
        var result = await WithProjectsAsync(p => p.CreateAsync(ProjectInputOf("Robot", target: Project.MaxTargetUnits + 1), Staff));

        AssertFailed(result, "project.target_too_large", ProjectFields.TargetUnits);
    }

    [Fact]
    public async Task Names_are_unique_ignoring_case()
    {
        await NewProjectAsync("Robot");

        var result = await WithProjectsAsync(p => p.CreateAsync(ProjectInputOf("ROBOT"), Staff));

        AssertFailed(result, "project.name_taken", ProjectFields.Name);
    }

    [Fact]
    public async Task Create_trims_text_and_stores_blank_optional_text_as_null()
    {
        var result = await WithProjectsAsync(p => p.CreateAsync(new ProjectInput("  Robot  ", "  ", "  ", 3, ""), Staff));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal("Robot", result.Value.Name);
        Assert.Null(result.Value.Description);
        Assert.Null(result.Value.Notes);
        Assert.Equal(3, result.Value.TargetUnits);
        Assert.Equal("PRJ-2026-0001", result.Value.Number);
    }

    [Fact]
    public async Task Create_publishes_an_event_and_a_failing_handler_does_not_break_it()
    {
        ProjectEvents.Throw = true;

        var project = await NewProjectAsync("Robot");

        var published = Assert.Single(ProjectEvents.Events);
        Assert.Equal(ProjectEventType.Created, published.Type);
        Assert.Equal(project.Id, published.ProjectId);
        Assert.Equal(project.Number, published.Number);
        Assert.Equal(UserId, published.UserId);
    }

    // ---- update / change number --------------------------------------------------------------------------------------

    [Fact]
    public async Task Update_changes_the_fields_but_never_the_number()
    {
        var project = await NewProjectAsync("Robot");

        var result = await WithProjectsAsync(p => p.UpdateAsync(
            project.Id, project.RowVersion, new ProjectInput("Robot 2", "SNEAKY-1", "Bigger", 5, "Notes")));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal("Robot 2", result.Value.Name);
        Assert.Equal(5, result.Value.TargetUnits);
        Assert.Equal(project.Number, result.Value.Number);
        Assert.True(result.Value.RowVersion > project.RowVersion);
    }

    [Fact]
    public async Task A_stale_row_version_is_a_concurrency_error_on_update()
    {
        var project = await NewProjectAsync("Robot");
        Assert.True((await WithProjectsAsync(p => p.UpdateAsync(project.Id, project.RowVersion, ProjectInputOf("Robot A")))).Succeeded);

        var result = await WithProjectsAsync(p => p.UpdateAsync(project.Id, project.RowVersion, ProjectInputOf("Robot B")));

        AssertFailed(result, "project.conflict");
        Assert.Equal("Robot A", (await DetailAsync(project.Id)).Name);
    }

    [Fact]
    public async Task Update_refuses_a_name_another_project_has()
    {
        await NewProjectAsync("Robot");
        var drone = await NewProjectAsync("Drone");

        var result = await WithProjectsAsync(p => p.UpdateAsync(drone.Id, drone.RowVersion, ProjectInputOf("robot")));

        AssertFailed(result, "project.name_taken", ProjectFields.Name);
    }

    [Fact]
    public async Task Update_of_a_missing_project_says_so()
    {
        var result = await WithProjectsAsync(p => p.UpdateAsync(9_999, 1, ProjectInputOf("Robot")));

        AssertFailed(result, "project.not_found");
    }

    [Fact]
    public async Task An_admin_can_change_the_number_and_the_bom_builds_and_ledger_are_untouched()
    {
        var project = await NewProjectAsync("Robot", number: "OLD-1");
        var part = await PartAsync("P1", onHand: 10);
        await AddPartAsync(project.Id, part, 2);
        Assert.True((await WithProjectsAsync(p => p.BuildAsync(project.Id, 1, null, Who))).Succeeded);
        var ledgerBefore = (await LedgerAsync()).Select(t => (t.Id, t.Reference)).ToList();
        var fresh = await DetailAsync(project.Id);

        var result = await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, fresh.RowVersion, "NEW-9", Admin));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal("NEW-9", result.Value.Number);
        Assert.True(result.Value.RowVersion > fresh.RowVersion);
        Assert.Single(result.Value.Lines);
        Assert.Equal(1, result.Value.BuiltUnits);
        Assert.Equal(ledgerBefore, (await LedgerAsync()).Select(t => (t.Id, t.Reference)).ToList());
    }

    [Fact]
    public async Task Changing_the_number_raises_an_event_with_old_and_new()
    {
        var project = await NewProjectAsync("Robot", number: "OLD-1");
        ProjectEvents.Events.Clear();

        await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, project.RowVersion, "NEW-1", Admin));

        var published = Assert.Single(ProjectEvents.Events);
        Assert.Equal(ProjectEventType.NumberChanged, published.Type);
        Assert.Equal("NEW-1", published.Number);
        Assert.Equal("OLD-1 -> NEW-1", published.Detail);
    }

    [Fact]
    public async Task Staff_cannot_change_the_number()
    {
        var project = await NewProjectAsync("Robot");

        var result = await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, project.RowVersion, "NEW-1", Staff));

        AssertFailed(result, "project.number_admin_only", ProjectFields.Number);
        Assert.Equal(project.Number, (await DetailAsync(project.Id)).Number);
    }

    [Fact]
    public async Task Changing_to_a_number_another_project_has_is_refused_in_any_case()
    {
        await NewProjectAsync("Robot", number: "ABC-1");
        var drone = await NewProjectAsync("Drone", number: "ABC-2");

        var result = await WithProjectsAsync(p => p.ChangeNumberAsync(drone.Id, drone.RowVersion, "abc-1", Admin));

        AssertFailed(result, "project.number_taken", ProjectFields.Number);
    }

    [Fact]
    public async Task Changing_to_the_same_number_changes_nothing()
    {
        var project = await NewProjectAsync("Robot", number: "ABC-1");
        ProjectEvents.Events.Clear();

        var result = await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, project.RowVersion, " ABC-1 ", Admin));

        Assert.True(result.Succeeded);
        Assert.Equal(project.RowVersion, result.Value.RowVersion);
        Assert.Empty(ProjectEvents.Events);
    }

    [Fact]
    public async Task Changing_the_number_needs_a_number_and_a_short_enough_one()
    {
        var project = await NewProjectAsync("Robot");

        AssertFailed(await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, project.RowVersion, "  ", Admin)), "project.number_required");
        AssertFailed(await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, project.RowVersion, new string('9', 31), Admin)), "project.number_too_long");
    }

    [Fact]
    public async Task A_stale_row_version_is_a_concurrency_error_on_change_number()
    {
        var project = await NewProjectAsync("Robot");
        Assert.True((await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, project.RowVersion, "A-1", Admin))).Succeeded);

        var result = await WithProjectsAsync(p => p.ChangeNumberAsync(project.Id, project.RowVersion, "B-1", Admin));

        AssertFailed(result, "project.conflict");
    }

    [Fact]
    public async Task Changing_the_number_never_touches_the_sequence()
    {
        var first = await NewProjectAsync("Robot");
        await WithProjectsAsync(p => p.ChangeNumberAsync(first.Id, first.RowVersion, "ELSEWHERE-1", Admin));

        var second = await NewProjectAsync("Drone");

        Assert.Equal("PRJ-2026-0002", second.Number);
    }

    // ---- search / archive / delete -----------------------------------------------------------------------------------

    [Fact]
    public async Task Search_finds_a_project_by_name_or_number_and_filters_by_tab()
    {
        var robot = await NewProjectAsync("Robot", number: "AAA-1");
        await NewProjectAsync("Drone", number: "BBB-2");
        var old = await NewProjectAsync("Old rig", number: "CCC-3");
        Assert.True((await WithProjectsAsync(p => p.SetActiveAsync(old.Id, false))).Succeeded);

        var byName = await WithProjectsAsync(p => p.SearchAsync(new ProjectListQuery { Search = "rob" }));
        var byNumber = await WithProjectsAsync(p => p.SearchAsync(new ProjectListQuery { Search = "bbb" }));
        var active = await WithProjectsAsync(p => p.SearchAsync(new ProjectListQuery()));
        var archived = await WithProjectsAsync(p => p.SearchAsync(new ProjectListQuery { Tab = ProjectTab.Archived }));
        var all = await WithProjectsAsync(p => p.SearchAsync(new ProjectListQuery { Tab = ProjectTab.All }));

        Assert.Equal(robot.Id, Assert.Single(byName.Items).Id);
        Assert.Equal("Drone", Assert.Single(byNumber.Items).Name);
        Assert.Equal(["Drone", "Robot"], active.Items.Select(i => i.Name));
        Assert.Equal("Old rig", Assert.Single(archived.Items).Name);
        Assert.Equal(3, all.TotalCount);
    }

    [Fact]
    public async Task The_list_shows_parts_built_cost_and_state()
    {
        var project = await NewProjectAsync("Robot", target: 3);
        var a = await PartAsync("A", onHand: 100, costCents: 250);
        var b = await PartAsync("B", onHand: 0, costCents: 1_000);
        await AddPartAsync(project.Id, a, 4);
        await AddPartAsync(project.Id, b, 1);
        await NewProjectAsync("Empty");

        var page = await WithProjectsAsync(p => p.SearchAsync(new ProjectListQuery { Sort = "name" }));

        var item = page.Items.Single(i => i.Id == project.Id);
        Assert.Equal(2, item.PartsCount);
        Assert.Equal(0, item.BuiltUnits);
        Assert.Equal(3, item.TargetUnits);
        Assert.Equal(4 * 250 + 1_000, item.BomCostCents);
        Assert.Equal(ProjectState.Short, item.Status.State);
        Assert.Equal(1, item.Status.Short);
        Assert.Equal(1, item.Status.Ready);
        Assert.Equal(ProjectState.Empty, page.Items.Single(i => i.Name == "Empty").Status.State);
    }

    [Fact]
    public async Task The_list_computes_status_for_the_whole_page_in_a_fixed_number_of_queries()
    {
        var part = await PartAsync("A", onHand: 5);
        for (var i = 0; i < 8; i++)
        {
            var project = await NewProjectAsync("Job " + i);
            await AddPartAsync(project.Id, part, 1);
        }

        var queries = 0;
        var (context, projects, _) = NewProjects(new CountingInterceptor(() => queries++));
        await using (context)
        {
            var page = await projects.SearchAsync(new ProjectListQuery());
            Assert.Equal(8, page.Items.Count);
        }

        Assert.True(queries <= 10, $"Expected a fixed number of queries, saw {queries}.");
    }

    [Fact]
    public async Task Archive_and_restore_are_idempotent()
    {
        var project = await NewProjectAsync("Robot");

        Assert.True((await WithProjectsAsync(p => p.SetActiveAsync(project.Id, false))).Succeeded);
        Assert.True((await WithProjectsAsync(p => p.SetActiveAsync(project.Id, false))).Succeeded);
        Assert.False((await DetailAsync(project.Id)).IsActive);
        Assert.True((await WithProjectsAsync(p => p.SetActiveAsync(project.Id, true))).Succeeded);
        Assert.True((await DetailAsync(project.Id)).IsActive);
        AssertFailed(await WithProjectsAsync(p => p.SetActiveAsync(9_999, false)), "project.not_found");
    }

    [Fact]
    public async Task A_project_without_builds_can_be_deleted_with_its_bom_and_the_parts_stay()
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync("A");
        await AddPartAsync(project.Id, part, 2);

        Assert.True((await WithProjectsAsync(p => p.DeleteAsync(project.Id))).Succeeded);

        Assert.Null(await WithProjectsAsync(p => p.GetAsync(project.Id)));
        await using var context = Db.CreateContext();
        Assert.Empty(await context.BomLines.ToListAsync());
        Assert.True(await context.Products.AnyAsync(p => p.Id == part));
        Assert.True((await WithProjectsAsync(p => p.DeleteAsync(project.Id))).Succeeded); // already gone
    }

    [Fact]
    public async Task A_project_with_a_build_cannot_be_deleted()
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync("A", onHand: 5);
        await AddPartAsync(project.Id, part, 1);
        Assert.True((await WithProjectsAsync(p => p.BuildAsync(project.Id, 1, null, Who))).Succeeded);

        var result = await WithProjectsAsync(p => p.DeleteAsync(project.Id));

        AssertFailed(result, "project.has_builds");
        Assert.NotNull(await WithProjectsAsync(p => p.GetAsync(project.Id)));
        Assert.False((await DetailAsync(project.Id)).CanDelete);
    }

    // ---- BOM lines ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_line_names_the_part_and_costs_quantity_times_unit_cost()
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync("A", costCents: 125);

        var line = await AddPartAsync(project.Id, part, 4, " near the motor ");

        Assert.Equal("A", line.Sku);
        Assert.Equal(4, line.QuantityPerUnit);
        Assert.Equal("near the motor", line.Note);
        Assert.Equal(500, line.LineCostCents);
        var detail = await DetailAsync(project.Id);
        Assert.Equal(500, detail.BomCostCents);
        var published = ProjectEvents.Events.Last();
        Assert.Equal(ProjectEventType.BomChanged, published.Type);
    }

    [Fact]
    public async Task A_part_can_be_in_the_bom_once_and_the_error_names_the_existing_line()
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync("A");
        await AddPartAsync(project.Id, part, 4);

        var result = await WithProjectsAsync(p => p.AddLineAsync(project.Id, new BomLineInput(part, 1, null), Staff));

        AssertFailed(result, "project.line_duplicate", ProjectFields.ProductId);
        Assert.Contains("A", result.Errors[0].Message);
        Assert.Contains("4 per unit", result.Errors[0].Message);
    }

    [Fact]
    public async Task The_same_part_can_be_in_two_projects()
    {
        var part = await PartAsync("A");
        var first = await NewProjectAsync("Robot");
        var second = await NewProjectAsync("Drone");

        await AddPartAsync(first.Id, part, 1);
        await AddPartAsync(second.Id, part, 9);

        Assert.Equal(9, (await DetailAsync(second.Id)).Lines.Single().QuantityPerUnit);
    }

    [Fact]
    public async Task An_archived_or_missing_part_cannot_be_added()
    {
        var project = await NewProjectAsync("Robot");
        var archived = await NewProductAsync("OLD", archived: true);

        AssertFailed(await WithProjectsAsync(p => p.AddLineAsync(project.Id, new BomLineInput(archived, 1, null), Staff)), "project.product_archived", ProjectFields.ProductId);
        AssertFailed(await WithProjectsAsync(p => p.AddLineAsync(project.Id, new BomLineInput(99_999, 1, null), Staff)), "project.product_missing", ProjectFields.ProductId);
        Assert.Empty((await DetailAsync(project.Id)).Lines);
    }

    [Fact]
    public async Task Add_line_to_a_missing_project_says_so()
    {
        var part = await PartAsync("A");

        AssertFailed(await WithProjectsAsync(p => p.AddLineAsync(9_999, new BomLineInput(part, 1, null), Staff)), "project.not_found");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(BomLine.MaxQuantityPerUnit + 1)]
    public async Task A_quantity_per_unit_outside_the_range_is_refused(int quantity)
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync("A");

        var result = await WithProjectsAsync(p => p.AddLineAsync(project.Id, new BomLineInput(part, quantity, null), Staff));

        Assert.False(result.Succeeded);
        Assert.Equal(ProjectFields.QuantityPerUnit, Assert.Single(result.Errors).Field);
    }

    [Fact]
    public async Task A_long_note_is_refused()
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync("A");

        var result = await WithProjectsAsync(p => p.AddLineAsync(project.Id, new BomLineInput(part, 1, new string('n', BomLine.NoteMaxLength + 1)), Staff));

        AssertFailed(result, "project.note_too_long", ProjectFields.Note);
    }

    [Fact]
    public async Task A_line_can_be_edited_in_place_and_removed_and_removing_twice_is_fine()
    {
        var project = await NewProjectAsync("Robot");
        var part = await PartAsync("A");
        var line = await AddPartAsync(project.Id, part, 1);

        var updated = await WithProjectsAsync(p => p.UpdateLineAsync(project.Id, line.Id, 7, "top", Staff));
        Assert.True(updated.Succeeded, Messages(updated));
        Assert.Equal(7, updated.Value.QuantityPerUnit);
        Assert.Equal("top", updated.Value.Note);

        AssertFailed(await WithProjectsAsync(p => p.UpdateLineAsync(project.Id, line.Id, 0, null, Staff)), "project.quantity_positive");
        AssertFailed(await WithProjectsAsync(p => p.UpdateLineAsync(project.Id, 424_242, 2, null, Staff)), "project.line_missing");

        Assert.True((await WithProjectsAsync(p => p.RemoveLineAsync(project.Id, line.Id, Staff))).Succeeded);
        Assert.True((await WithProjectsAsync(p => p.RemoveLineAsync(project.Id, line.Id, Staff))).Succeeded);
        Assert.Empty((await DetailAsync(project.Id)).Lines);
    }

    [Fact]
    public async Task A_line_of_another_project_cannot_be_edited_through_this_one()
    {
        var first = await NewProjectAsync("Robot");
        var second = await NewProjectAsync("Drone");
        var part = await PartAsync("A");
        var line = await AddPartAsync(first.Id, part, 1);

        AssertFailed(await WithProjectsAsync(p => p.UpdateLineAsync(second.Id, line.Id, 5, null, Staff)), "project.line_missing");
        Assert.True((await WithProjectsAsync(p => p.RemoveLineAsync(second.Id, line.Id, Staff))).Succeeded);
        Assert.Single((await DetailAsync(first.Id)).Lines);
    }

    // ---- quick-create a part -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_part_is_created_in_the_catalog_and_added_in_one_step()
    {
        var project = await NewProjectAsync("Robot");

        var result = await WithProjectsAsync(p => p.CreatePartAndAddAsync(project.Id, new NewPartInput("NEW-1", "Brand new", null, 480), 3, Staff));

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal("NEW-1", result.Value.Sku);
        Assert.Equal(3, result.Value.QuantityPerUnit);
        await using var context = Db.CreateContext();
        var product = await context.Products.SingleAsync(p => p.Sku == "NEW-1");
        Assert.Equal("each", product.Unit);
        Assert.Equal(480, product.UnitCostCents);
        Assert.Equal(0, product.QuantityOnHand);
        Assert.True(product.IsActive);
    }

    [Fact]
    public async Task A_duplicate_sku_creates_neither_the_part_nor_the_line()
    {
        var project = await NewProjectAsync("Robot");
        await PartAsync("TAKEN");

        var result = await WithProjectsAsync(p => p.CreatePartAndAddAsync(project.Id, new NewPartInput("taken", "Again", null, 0), 1, Staff));

        Assert.False(result.Succeeded);
        Assert.Equal(ProductFields.Sku, Assert.Single(result.Errors).Field);
        await using var context = Db.CreateContext();
        Assert.Equal(1, await context.Products.CountAsync());
        Assert.Empty(await context.BomLines.ToListAsync());
    }

    [Fact]
    public async Task A_new_part_for_a_missing_project_or_with_a_bad_quantity_creates_nothing()
    {
        var project = await NewProjectAsync("Robot");

        AssertFailed(await WithProjectsAsync(p => p.CreatePartAndAddAsync(9_999, new NewPartInput("X-1", "X", null, 0), 1, Staff)), "project.not_found");
        AssertFailed(await WithProjectsAsync(p => p.CreatePartAndAddAsync(project.Id, new NewPartInput("X-2", "X", null, 0), 0, Staff)), "project.quantity_positive");

        await using var context = Db.CreateContext();
        Assert.Empty(await context.Products.ToListAsync());
    }

    // ---- stock invariant ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Nothing_here_changes_stock()
    {
        var part = await PartAsync("A", onHand: 7);
        var project = await NewProjectAsync("Robot");
        await AddPartAsync(project.Id, part, 3);
        await WithProjectsAsync(p => p.UpdateLineAsync(project.Id, 1, 5, null, Staff));

        Assert.Equal(7, await OnHandAsync(part));
        Assert.Single(await LedgerAsync(part));
    }
}
