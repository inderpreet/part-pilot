using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-10-2: building units issues the parts from stock atomically (F10 FR-7).</summary>
public sealed class ProjectBuildTests : ProjectTestBase
{
    private async Task<(int ProjectId, int A, int B)> TwoPartProjectAsync(int target = 5, int aOnHand = 20, int bOnHand = 20)
    {
        var project = await NewProjectAsync("Robot", target);
        var a = await PartAsync("A", aOnHand);
        var b = await PartAsync("B", bOnHand);
        await AddPartAsync(project.Id, a, 2);
        await AddPartAsync(project.Id, b, 3);
        return (project.Id, a, b);
    }

    private async Task<Result<BuildResult>> BuildAsync(int projectId, int units, string? note = null)
        => await WithProjectsAsync(p => p.BuildAsync(projectId, units, note, Who));

    [Fact]
    public async Task A_build_issues_every_part_posts_the_reference_and_records_the_build()
    {
        var (projectId, a, b) = await TwoPartProjectAsync();

        var result = await BuildAsync(projectId, 3, "first batch");

        Assert.True(result.Succeeded, Messages(result));
        Assert.Equal(2, result.Value.Movements.Count);
        Assert.Equal(2, result.Value.RemainingUnits);
        Assert.Equal(14, await OnHandAsync(a)); // 20 - 2 x 3
        Assert.Equal(11, await OnHandAsync(b)); // 20 - 3 x 3

        var issues = (await LedgerAsync()).Where(t => t.Type == StockTransactionType.Issue).ToList();
        Assert.Equal(2, issues.Count);
        Assert.All(issues, t =>
        {
            Assert.Equal("Build", t.Reason);
            Assert.Equal($"Robot build #{result.Value.BuildId}", t.Reference);
            Assert.Equal("first batch", t.Note);
            Assert.Equal(UserId, t.UserId);
        });
        Assert.Equal([-6, -9], issues.Select(t => t.QuantityDelta));

        var build = Assert.Single(await BuildRowsAsync(projectId));
        Assert.Equal(result.Value.BuildId, build.Id);
        Assert.Equal(3, build.Units);
        Assert.Equal(UserId, build.UserId);
        Assert.Equal("first batch", build.Note);
        await AssertConsistentAsync(a);
        await AssertConsistentAsync(b);
    }

    [Fact]
    public async Task One_short_part_refuses_the_build_and_nothing_changes()
    {
        var (projectId, a, b) = await TwoPartProjectAsync(aOnHand: 20, bOnHand: 4);
        var ledgerBefore = (await LedgerAsync()).Count;

        var result = await BuildAsync(projectId, 3);

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal("project.build_short", error.Code);
        Assert.Contains("B", error.Message);
        Assert.Contains("only 4 on hand", error.Message);
        Assert.Empty(await BuildRowsAsync(projectId));
        Assert.Equal(ledgerBefore, (await LedgerAsync()).Count);
        Assert.Equal(20, await OnHandAsync(a));
        Assert.Equal(4, await OnHandAsync(b));
    }

    [Fact]
    public async Task Every_short_part_gets_its_own_error()
    {
        var (projectId, _, _) = await TwoPartProjectAsync(aOnHand: 1, bOnHand: 0);

        var result = await BuildAsync(projectId, 1);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Message.StartsWith("A:") && e.Message.Contains("only 1 on hand"));
        Assert.Contains(result.Errors, e => e.Message.StartsWith("B:") && e.Message.Contains("none on hand"));
    }

    [Fact]
    public async Task A_build_never_overrides_the_negative_stock_block_even_when_negative_stock_is_allowed()
    {
        var (projectId, a, _) = await TwoPartProjectAsync(aOnHand: 1, bOnHand: 20);
        Settings.AllowNegative = true;

        var result = await BuildAsync(projectId, 1);

        Assert.False(result.Succeeded);
        Assert.Equal(1, await OnHandAsync(a));
    }

    [Fact]
    public async Task The_whole_build_rolls_back_when_the_stock_post_is_refused()
    {
        var (projectId, a, b) = await TwoPartProjectAsync();
        await ArchiveAsync(b); // The pre-check names it; this proves nothing leaks when the part is refused.

        var result = await BuildAsync(projectId, 1);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, e => e.Code == "project.part_archived");
        Assert.Empty(await BuildRowsAsync(projectId));
        Assert.Equal(20, await OnHandAsync(a));
    }

    [Fact]
    public async Task A_build_joins_the_callers_transaction_so_rolling_that_back_undoes_the_issues_and_the_build_row()
    {
        var (projectId, a, b) = await TwoPartProjectAsync();

        var (context, projects, _) = NewProjects();
        await using (context)
        {
            await using var outer = await context.Database.BeginTransactionAsync();
            var result = await projects.BuildAsync(projectId, 2, null, Who);
            Assert.True(result.Succeeded, Messages(result));
            Assert.Equal(16, await context.Products.Where(p => p.Id == a).Select(p => p.QuantityOnHand).SingleAsync());
            // Disposed without committing: everything above is rolled back together.
        }

        Assert.Empty(await BuildRowsAsync(projectId));
        Assert.Equal(20, await OnHandAsync(a));
        Assert.Equal(20, await OnHandAsync(b));
        Assert.DoesNotContain(await LedgerAsync(), t => t.Type == StockTransactionType.Issue);
    }

    [Fact]
    public async Task On_hand_is_judged_inside_the_transaction_not_on_what_a_page_showed()
    {
        var (projectId, a, _) = await TwoPartProjectAsync(target: 1, aOnHand: 4);
        var staleStatus = await StatusAsync(projectId);
        Assert.Equal(LineState.Ready, staleStatus.Lines[0].State);
        Assert.True((await PostAsync(new StockMovement(a, StockTransactionType.Issue, 3))).Succeeded);

        var result = await BuildAsync(projectId, 1);

        Assert.False(result.Succeeded);
        Assert.Equal(1, await OnHandAsync(a));
    }

    [Fact]
    public async Task Units_must_be_at_least_one_and_at_most_the_limit()
    {
        var (projectId, _, _) = await TwoPartProjectAsync();

        AssertFailed(await BuildAsync(projectId, 0), "project.units_positive", ProjectFields.Units);
        AssertFailed(await BuildAsync(projectId, -2), "project.units_positive", ProjectFields.Units);
        AssertFailed(await BuildAsync(projectId, Project.MaxTargetUnits + 1), "project.units_too_large", ProjectFields.Units);
    }

    [Fact]
    public async Task A_project_without_parts_cannot_be_built()
    {
        var project = await NewProjectAsync("Empty");

        AssertFailed(await BuildAsync(project.Id, 1), "project.no_lines");
    }

    [Fact]
    public async Task A_missing_project_cannot_be_built()
        => AssertFailed(await BuildAsync(9_999, 1), "project.not_found");

    [Fact]
    public async Task A_long_note_is_refused()
    {
        var (projectId, _, _) = await TwoPartProjectAsync();

        AssertFailed(await BuildAsync(projectId, 1, new string('n', 501)), "project.note_too_long", ProjectFields.Note);
    }

    [Fact]
    public async Task A_long_project_name_is_shortened_to_fit_the_reference_column()
    {
        var project = await NewProjectAsync(new string('N', 100));
        var part = await PartAsync("A", 5);
        await AddPartAsync(project.Id, part, 1);

        var result = await BuildAsync(project.Id, 1);

        Assert.True(result.Succeeded, Messages(result));
        var reference = Assert.Single(await LedgerAsync(part), t => t.Type == StockTransactionType.Issue).Reference!;
        Assert.True(reference.Length <= StockTransaction.ReferenceMaxLength);
        Assert.EndsWith($" build #{result.Value.BuildId}", reference);
    }

    [Fact]
    public async Task Builds_accumulate_and_are_listed_newest_first_with_who_did_them()
    {
        var (projectId, _, _) = await TwoPartProjectAsync(target: 5);
        var first = await BuildAsync(projectId, 2);
        var second = await BuildAsync(projectId, 1, "second");

        var builds = await WithProjectsAsync(p => p.GetBuildsAsync(projectId));

        Assert.Equal([second.Value.BuildId, first.Value.BuildId], builds.Select(b => b.Id));
        Assert.Equal([1, 2], builds.Select(b => b.Units));
        Assert.Equal("Mo Ver", builds[0].UserName);
        Assert.Equal("second", builds[0].Note);
        Assert.Equal(3, (await DetailAsync(projectId)).BuiltUnits);
        Assert.Equal(2, (await DetailAsync(projectId)).RemainingUnits);
        Assert.Equal(2, (await DetailAsync(projectId)).BuildCount);
    }

    [Fact]
    public async Task A_build_raises_an_event_and_a_failing_handler_does_not_undo_it()
    {
        var (projectId, a, _) = await TwoPartProjectAsync();
        ProjectEvents.Throw = true;
        ProjectEvents.Events.Clear();

        var result = await BuildAsync(projectId, 1);

        Assert.True(result.Succeeded, Messages(result));
        var published = Assert.Single(ProjectEvents.Events);
        Assert.Equal(ProjectEventType.Built, published.Type);
        Assert.Contains($"build #{result.Value.BuildId}", published.Detail);
        Assert.Equal(18, await OnHandAsync(a));
    }

    [Fact]
    public async Task Part_of_the_issue_can_be_reversed_from_the_stock_history_without_touching_the_build_row()
    {
        var (projectId, a, _) = await TwoPartProjectAsync();
        Assert.True((await BuildAsync(projectId, 1)).Succeeded);
        var issue = (await LedgerAsync(a)).Single(t => t.Type == StockTransactionType.Issue);

        var reversed = await ReverseAsync(issue.Id);

        Assert.True(reversed.Succeeded, Messages(reversed));
        Assert.Equal(20, await OnHandAsync(a));
        Assert.Single(await BuildRowsAsync(projectId));
        await AssertConsistentAsync(a);
    }

    [Fact]
    public async Task The_ledger_and_the_cache_agree_after_a_build_and_the_stock_check_passes()
    {
        var (projectId, a, b) = await TwoPartProjectAsync();
        await BuildAsync(projectId, 4);

        var (context, stock) = NewStock();
        await using (context)
        {
            var check = await stock.VerifyAsync();
            Assert.True(check.IsConsistent, "Invariant 1 broken by a build.");
        }

        await AssertConsistentAsync(a);
        await AssertConsistentAsync(b);
    }
}
