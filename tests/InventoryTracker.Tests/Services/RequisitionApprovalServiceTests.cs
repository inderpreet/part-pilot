using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-12-2: deciding and cancelling requisitions, and the approval queue.</summary>
public sealed class RequisitionApprovalServiceTests : PurchaseOrderTestBase
{
    private RecordingRequisitionEvents Recorded { get; } = new();

    private string _requesterId = null!;

    private RequisitionActor Requester => new(_requesterId);

    private RequisitionActor Manager => new(UserId, CanSeeAll: true, CanApprove: true);

    private RequisitionActor Admin => new(UserId, CanSeeAll: true, IsAdmin: true, CanApprove: true);

    private RequisitionActor Staff => new(UserId, CanSeeAll: true);

    private async Task UsersAsync()
    {
        await using var context = Db.CreateContext();
        var requester = new ApplicationUser { UserName = "asker", DisplayName = "Ann Asker" };
        context.Users.Add(requester);
        await context.SaveChangesAsync();
        _requesterId = requester.Id;
    }

    private async Task<T> WithAsync<T>(Func<RequisitionService, Task<T>> action)
    {
        var (context, products, _) = NewRequest();
        await using (context)
        {
            return await action(new RequisitionService(context, products, Settings, Clock, new RequisitionEventPublisher([Recorded], NullLogger<RequisitionEventPublisher>.Instance)));
        }
    }

    private static void Ok(Result result) => Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));

    private static void Refused(Result result, string code)
    {
        Assert.False(result.Succeeded, "Expected the service to refuse.");
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    private async Task<int> PartAsync(string sku) => (await CreateProductAsync(Input(sku: sku, name: "Part " + sku))).Id;

    /// <summary>A submitted requisition with one line per quantity, each 1 of the parts created here.</summary>
    private async Task<RequisitionDetail> SubmittedAsync(RequisitionActor actor, params int[] quantities)
    {
        var created = await WithAsync(s => s.CreateDraftAsync(new RequisitionHeaderInput(null, "Line stopped"), actor));
        Ok(created);
        foreach (var quantity in quantities)
        {
            var part = await PartAsync($"P-{created.Value.Id}-{Guid.NewGuid():N}"[..20]);
            Ok(await WithAsync(s => s.AddLineAsync(created.Value.Id, new RequisitionLineInput(part, quantity), actor)));
        }

        var draft = (await WithAsync(s => s.GetAsync(created.Value.Id, actor)))!;
        var submitted = await WithAsync(s => s.SubmitAsync(draft.Id, draft.RowVersion, actor));
        Ok(submitted);
        return submitted.Value;
    }

    private static RequisitionLineDecisionInput Approve(RequisitionLineItem line, int? quantity = null, string? reason = null)
        => new(line.Id, RequisitionDecision.Approved, quantity ?? line.QuantityRequested, reason);

    private static RequisitionLineDecisionInput Reject(RequisitionLineItem line, string? reason = "Not in budget")
        => new(line.Id, RequisitionDecision.Rejected, null, reason);

    private Task<Result<RequisitionDetail>> DecideAsync(RequisitionDetail requisition, RequisitionActor actor, params RequisitionLineDecisionInput[] decisions)
        => WithAsync(s => s.DecideAsync(requisition.Id, requisition.RowVersion, decisions, actor));

    private async Task<RequisitionStatus> StatusAsync(int id)
    {
        await using var context = Db.CreateContext();
        return (await context.Requisitions.SingleAsync(r => r.Id == id)).Status;
    }

    private async Task<int> DecisionCountAsync()
    {
        await using var context = Db.CreateContext();
        return await context.RequisitionLineDecisions.CountAsync();
    }

    [Fact]
    public async Task Approving_every_line_approves_the_requisition()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4, 2);

        var result = await DecideAsync(requisition, Manager, Approve(requisition.Lines[0]), Approve(requisition.Lines[1]));

        Ok(result);
        Assert.Equal(RequisitionStatus.Approved, result.Value.Status);
        Assert.Equal(Clock.UtcNow, result.Value.DecidedAt);
        Assert.Equal([4, 2], result.Value.Lines.Select(l => l.Outcome!.QuantityApproved));
        Assert.False(result.Value.CanDecide);
        Assert.False(result.Value.DecidedByRequester);
    }

    [Fact]
    public async Task A_mix_of_approved_reduced_and_rejected_lines_saves_together()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4, 5, 6);

        var result = await DecideAsync(
            requisition, Manager,
            Approve(requisition.Lines[0]),
            Approve(requisition.Lines[1], 3, "  Only three fit  "),
            Reject(requisition.Lines[2]));

        Ok(result);
        Assert.Equal(RequisitionStatus.Approved, result.Value.Status);
        Assert.Equal([4, 3, 0], result.Value.Lines.Select(l => l.Outcome!.QuantityApproved));
        Assert.Equal("Only three fit", result.Value.Lines[1].Outcome!.Reason);
        Assert.Equal(RequisitionDecision.Rejected, result.Value.Lines[2].Outcome!.Decision);
    }

    [Fact]
    public async Task Rejecting_every_line_rejects_the_requisition()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4);

        var result = await DecideAsync(requisition, Manager, Reject(requisition.Lines[0]));

        Ok(result);
        Assert.Equal(RequisitionStatus.Rejected, result.Value.Status);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task An_approved_quantity_outside_one_to_requested_is_refused(int quantity)
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4);

        var result = await DecideAsync(requisition, Manager, Approve(requisition.Lines[0], quantity, "reason"));

        Refused(result, "requisition.approved_quantity_invalid");
        Assert.Equal("Lines[0].QuantityApproved", result.Errors[0].Field);
        Assert.Equal(RequisitionStatus.Submitted, await StatusAsync(requisition.Id));
    }

    [Fact]
    public async Task A_rejection_or_reduction_without_a_reason_is_refused_on_its_own_row()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4, 5);

        var result = await DecideAsync(requisition, Manager, Reject(requisition.Lines[0], "   "), Approve(requisition.Lines[1], 2));

        Refused(result, "requisition.decision_reason_required");
        Assert.Equal(["Lines[0].Reason", "Lines[1].Reason"], result.Errors.Select(e => e.Field));
        Assert.Equal(0, await DecisionCountAsync());
    }

    [Fact]
    public async Task A_reason_that_is_too_long_is_refused()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4);

        Refused(await DecideAsync(requisition, Manager, Reject(requisition.Lines[0], new string('x', RequisitionLineDecision.ReasonMaxLength + 1))), "requisition.decision_reason_too_long");
    }

    [Fact]
    public async Task Every_line_must_be_decided_and_nothing_saves_otherwise()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4, 5);

        Refused(await DecideAsync(requisition, Manager, Approve(requisition.Lines[0])), "requisition.line_undecided");
        Refused(await DecideAsync(requisition, Manager, Approve(requisition.Lines[0]), new RequisitionLineDecisionInput(requisition.Lines[1].Id, null, 5, null)), "requisition.decision_required");

        Assert.Equal(0, await DecisionCountAsync());
        Assert.Equal(RequisitionStatus.Submitted, await StatusAsync(requisition.Id));
    }

    [Fact]
    public async Task A_line_from_elsewhere_or_listed_twice_is_refused()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 4);
        var other = await SubmittedAsync(Requester, 1);

        Refused(await DecideAsync(requisition, Manager, Approve(other.Lines[0])), "requisition.line_missing");
        Refused(await DecideAsync(requisition, Manager, Approve(requisition.Lines[0]), Approve(requisition.Lines[0])), "requisition.line_missing");
    }

    [Fact]
    public async Task A_Manager_or_Admin_can_decide_their_own_and_it_shows()
    {
        await UsersAsync();
        var managers = await SubmittedAsync(Manager, 2);
        var admins = await SubmittedAsync(Admin, 2);

        var decidedByManager = await DecideAsync(managers, Manager, Approve(managers.Lines[0]));
        var decidedByAdmin = await DecideAsync(admins, Admin, Approve(admins.Lines[0]));

        Ok(decidedByManager);
        Ok(decidedByAdmin);
        Assert.True(decidedByManager.Value.DecidedByRequester);
        Assert.True(decidedByAdmin.Value.DecidedByRequester);
    }

    [Fact]
    public async Task A_decision_is_final()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 2);
        var decided = await DecideAsync(requisition, Manager, Approve(requisition.Lines[0]));
        Ok(decided);

        Refused(await DecideAsync(requisition, Manager, Reject(requisition.Lines[0])), "requisition.conflict");
        Refused(await DecideAsync(decided.Value, Manager, Reject(requisition.Lines[0])), "requisition.not_submitted");
        Refused(await WithAsync(s => s.WithdrawAsync(requisition.Id, decided.Value.RowVersion, Requester)), "requisition.not_submitted");
        Assert.Equal(1, await DecisionCountAsync());
    }

    [Fact]
    public async Task Staff_and_Viewers_cannot_decide_or_cancel()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 2);

        foreach (var actor in new[] { Staff, Requester })
        {
            Refused(await DecideAsync(requisition, actor, Approve(requisition.Lines[0])), "requisition.not_approver");
            Refused(await WithAsync(s => s.CancelSubmittedAsync(requisition.Id, requisition.RowVersion, "No", actor)), "requisition.not_approver");
        }

        Assert.Equal(RequisitionStatus.Submitted, await StatusAsync(requisition.Id));
    }

    [Fact]
    public async Task A_draft_cannot_be_decided_or_cancelled()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 2);
        var withdrawn = await WithAsync(s => s.WithdrawAsync(requisition.Id, requisition.RowVersion, Requester));
        Ok(withdrawn);

        Refused(await DecideAsync(withdrawn.Value, Manager, Approve(requisition.Lines[0])), "requisition.not_submitted");
        Refused(await WithAsync(s => s.CancelSubmittedAsync(withdrawn.Value.Id, withdrawn.Value.RowVersion, "No", Manager)), "requisition.not_submitted");
    }

    [Fact]
    public async Task A_decision_changes_neither_stock_nor_on_order()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 2);

        var result = await DecideAsync(requisition, Manager, Approve(requisition.Lines[0]));

        Ok(result);
        Assert.Equal(0, result.Value.Lines[0].OnHand);
        Assert.Equal(0, result.Value.Lines[0].OnOrder);
        await using var context = Db.CreateContext();
        Assert.Equal(0, await context.StockTransactions.CountAsync());
        Assert.Equal(0, await context.PurchaseOrders.CountAsync());
    }

    [Fact]
    public async Task Cancelling_needs_a_reason_and_records_who_and_why()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 2);

        Refused(await WithAsync(s => s.CancelSubmittedAsync(requisition.Id, requisition.RowVersion, "  ", Manager)), "requisition.cancel_reason_required");
        Refused(await WithAsync(s => s.CancelSubmittedAsync(requisition.Id, requisition.RowVersion, new string('x', RequisitionCancellation.ReasonMaxLength + 1), Manager)), "requisition.cancel_reason_too_long");

        var cancelled = await WithAsync(s => s.CancelSubmittedAsync(requisition.Id, requisition.RowVersion, " Project dropped ", Manager));

        Ok(cancelled);
        Assert.Equal(RequisitionStatus.Cancelled, cancelled.Value.Status);
        Assert.Equal("Project dropped", cancelled.Value.Cancellation!.Reason);
        Assert.Null(cancelled.Value.DecidedAt);
        Refused(await WithAsync(s => s.CancelSubmittedAsync(requisition.Id, cancelled.Value.RowVersion, "Again", Manager)), "requisition.not_submitted");
    }

    [Fact]
    public async Task A_stale_page_cannot_cancel()
    {
        await UsersAsync();
        var requisition = await SubmittedAsync(Requester, 2);

        Refused(await WithAsync(s => s.CancelSubmittedAsync(requisition.Id, requisition.RowVersion + 1, "No", Manager)), "requisition.conflict");
    }

    [Fact]
    public async Task Decided_and_cancelled_are_published()
    {
        await UsersAsync();
        var decided = await SubmittedAsync(Requester, 2);
        var cancelled = await SubmittedAsync(Requester, 2);
        Recorded.Events.Clear();

        Ok(await DecideAsync(decided, Manager, Approve(decided.Lines[0])));
        Ok(await WithAsync(s => s.CancelSubmittedAsync(cancelled.Id, cancelled.RowVersion, "No", Manager)));

        Assert.Equal([RequisitionEventType.Decided, RequisitionEventType.Cancelled], Recorded.Events.Select(e => e.Type));
        Assert.Equal(decided.Id, Recorded.Events[0].RequisitionId);
    }

    [Fact]
    public async Task The_queue_is_oldest_first_with_an_estimate_and_marks_your_own()
    {
        await UsersAsync();
        var newer = await SubmittedAsync(Requester, 3);
        var older = await SubmittedAsync(Manager, 2, 4);
        var decided = await SubmittedAsync(Requester, 1);
        Ok(await DecideAsync(decided, Manager, Approve(decided.Lines[0])));
        await using (var context = Db.CreateContext())
        {
            await context.Products.ExecuteUpdateAsync(s => s.SetProperty(p => p.UnitCostCents, 150L));
            await context.Requisitions.Where(r => r.Id == older.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.SubmittedAt, Clock.UtcNow.AddDays(-2)));
        }

        var queue = await WithAsync(s => s.GetApprovalQueueAsync(new ListQuery(), Manager));

        Assert.Equal([older.Id, newer.Id], queue.Items.Select(i => i.Id));
        Assert.Equal(2, queue.Items[0].LineCount);
        Assert.Equal(900, queue.Items[0].EstimatedValueCents);
        Assert.True(queue.Items[0].IsOwn);
        Assert.False(queue.Items[1].IsOwn);
        Assert.Equal(2, await WithAsync(s => s.CountAwaitingDecisionAsync()));
    }

    [Fact]
    public async Task The_queue_is_empty_for_someone_who_cannot_decide()
    {
        await UsersAsync();
        await SubmittedAsync(Requester, 2);

        Assert.Empty((await WithAsync(s => s.GetApprovalQueueAsync(new ListQuery(), Staff))).Items);
    }
}

/// <summary>Real file database, one connection per request, as in production: two people deciding at once.</summary>
public sealed class RequisitionApprovalConcurrencyTests : IAsyncLifetime
{
    private string _path = null!;
    private string _userId = null!;
    private int _productId;

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"inventory-req-{Guid.NewGuid():N}.db");
        await using var context = NewContext();
        await context.Database.MigrateAsync();
        var user = new ApplicationUser { UserName = "boss", DisplayName = "Bo Ss" };
        var product = new Product { Sku = "RACE-1", Name = "Racing widget" };
        context.AddRange(user, product);
        await context.SaveChangesAsync();
        _userId = user.Id;
        _productId = product.Id;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }

    private AppDbContext NewContext()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.UseInventorySqlite($"Data Source={_path}");
        return new AppDbContext(builder.Options);
    }

    private static RequisitionService NewService(AppDbContext context) => new(
        context,
        new ProductService(context),
        new FakeSettings(),
        new TestClock(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc)),
        new RequisitionEventPublisher([], NullLogger<RequisitionEventPublisher>.Instance));

    [Fact]
    public async Task Of_two_simultaneous_decisions_one_wins_and_the_other_gets_a_conflict()
    {
        var actor = new RequisitionActor(_userId, CanSeeAll: true, CanApprove: true);
        RequisitionDetail requisition;
        await using (var context = NewContext())
        {
            var service = NewService(context);
            var created = await service.CreateDraftAsync(new RequisitionHeaderInput(null, "Need"), actor);
            Assert.True((await service.AddLineAsync(created.Value.Id, new RequisitionLineInput(_productId, 2), actor)).Succeeded);
            var draft = (await service.GetAsync(created.Value.Id, actor))!;
            requisition = (await service.SubmitAsync(draft.Id, draft.RowVersion, actor)).Value;
        }

        var gate = new TaskCompletionSource();
        var tasks = new[] { RequisitionDecision.Approved, RequisitionDecision.Rejected }.Select(decision => Task.Run(async () =>
        {
            await gate.Task;
            await using var context = NewContext();
            return await NewService(context).DecideAsync(
                requisition.Id, requisition.RowVersion, [new RequisitionLineDecisionInput(requisition.Lines[0].Id, decision, 2, "Because")], actor);
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.Contains(results.Single(r => !r.Succeeded).Errors, e => e.Code == "requisition.conflict");
        await using var read = NewContext();
        Assert.Equal(1, await read.RequisitionLineDecisions.CountAsync());
    }
}
