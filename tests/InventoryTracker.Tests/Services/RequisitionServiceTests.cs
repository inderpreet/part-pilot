using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

public sealed class RecordingRequisitionEvents : IRequisitionEvents
{
    public List<RequisitionEvent> Events { get; } = [];

    public bool Throw { get; set; }

    public Task HandleAsync(RequisitionEvent requisitionEvent, CancellationToken cancellationToken = default)
    {
        Events.Add(requisitionEvent);
        return Throw ? throw new InvalidOperationException("handler failed") : Task.CompletedTask;
    }
}

/// <summary>WI-11-2: requisition rules, ownership per role, status transitions and numbering.</summary>
public sealed class RequisitionServiceTests : PurchaseOrderTestBase
{
    private RecordingRequisitionEvents Recorded { get; } = new();

    private string _viewerId = null!;
    private string _otherId = null!;

    private RequisitionActor Viewer => new(_viewerId);

    private RequisitionActor OtherViewer => new(_otherId);

    private RequisitionActor StaffActor => new(UserId, CanSeeAll: true);

    private RequisitionActor Admin => new(UserId + "-admin", CanSeeAll: true, IsAdmin: true);

    private async Task UsersAsync()
    {
        await using var context = Db.CreateContext();
        var viewer = new ApplicationUser { UserName = "viewer", DisplayName = "Vi Ewer" };
        var other = new ApplicationUser { UserName = "other", DisplayName = "Oth Er" };
        var admin = new ApplicationUser { Id = UserId + "-admin", UserName = "root", DisplayName = "Ad Min" };
        context.Users.AddRange(viewer, other, admin);
        await context.SaveChangesAsync();
        _viewerId = viewer.Id;
        _otherId = other.Id;
    }

    private (AppDbContext Context, RequisitionService Service) NewService()
    {
        var (context, products, _) = NewRequest();
        return (context, new RequisitionService(context, products, Settings, Clock, new RequisitionEventPublisher([Recorded], NullLogger<RequisitionEventPublisher>.Instance)));
    }

    private async Task<T> WithAsync<T>(Func<RequisitionService, Task<T>> action)
    {
        var (context, service) = NewService();
        await using (context)
        {
            return await action(service);
        }
    }

    private static void Ok(Result result) => Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));

    private static void Refused(Result result, string code)
    {
        Assert.False(result.Succeeded, "Expected the service to refuse.");
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    private async Task<int> PartAsync(string sku, int onHand = 0)
    {
        var id = (await CreateProductAsync(Input(sku: sku, name: "Part " + sku))).Id;
        if (onHand > 0)
        {
            Assert.True((await PostAsync(Receive(id, onHand))).Succeeded);
        }

        return id;
    }

    private async Task<RequisitionDetail> DraftAsync(RequisitionActor actor, string? reason = "Line stopped", params (int ProductId, int Quantity)[] lines)
    {
        var created = await WithAsync(s => s.CreateDraftAsync(new RequisitionHeaderInput(null, reason), actor));
        Ok(created);
        foreach (var (productId, quantity) in lines)
        {
            Ok(await WithAsync(s => s.AddLineAsync(created.Value.Id, new RequisitionLineInput(productId, quantity), actor)));
        }

        return (await WithAsync(s => s.GetAsync(created.Value.Id, actor)))!;
    }

    private async Task<RequisitionDetail> SubmittedAsync(RequisitionActor actor, int productId)
    {
        var draft = await DraftAsync(actor, "Line stopped", (productId, 2));
        var submitted = await WithAsync(s => s.SubmitAsync(draft.Id, draft.RowVersion, actor));
        Ok(submitted);
        return submitted.Value;
    }

    private async Task SetStatusAsync(int id, RequisitionStatus status)
    {
        await using var context = Db.CreateContext();
        await context.Requisitions.Where(r => r.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, status));
    }

    // ---- numbering and creation --------------------------------------------------------------------------------

    [Fact]
    public async Task Numbers_use_the_prefix_and_year_and_count_up()
    {
        await UsersAsync();

        var first = await DraftAsync(Viewer);
        var second = await DraftAsync(Viewer);

        Assert.Equal("REQ-2026-0001", first.Number);
        Assert.Equal("REQ-2026-0002", second.Number);
    }

    [Fact]
    public async Task A_new_draft_belongs_to_the_actor_and_raises_an_event()
    {
        await UsersAsync();

        var draft = await DraftAsync(Viewer);

        Assert.Equal(RequisitionStatus.Draft, draft.Status);
        Assert.Equal(_viewerId, draft.RequestedById);
        Assert.Equal("Vi Ewer", draft.RequestedByName);
        Assert.Equal(RequisitionEventType.Created, Assert.Single(Recorded.Events).Type);
    }

    [Fact]
    public async Task A_failing_event_handler_does_not_undo_the_change()
    {
        await UsersAsync();
        Recorded.Throw = true;

        var draft = await DraftAsync(Viewer);

        Assert.NotNull(await WithAsync(s => s.GetAsync(draft.Id, Viewer)));
    }

    [Fact]
    public async Task A_reason_over_the_limit_is_refused()
    {
        await UsersAsync();

        var result = await WithAsync(s => s.CreateDraftAsync(new RequisitionHeaderInput(null, new string('x', Requisition.ReasonMaxLength + 1)), Viewer));

        Refused(result, "requisition.reason_too_long");
    }

    // ---- lines -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "requisition.quantity_invalid")]
    [InlineData(-3, "requisition.quantity_invalid")]
    [InlineData(RequisitionLine.MaxQuantity + 1, "requisition.quantity_too_large")]
    public async Task A_quantity_out_of_range_is_refused(int quantity, string code)
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var draft = await DraftAsync(Viewer);

        Refused(await WithAsync(s => s.AddLineAsync(draft.Id, new RequisitionLineInput(part, quantity), Viewer)), code);
    }

    [Fact]
    public async Task One_line_per_part()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var draft = await DraftAsync(Viewer, "Why", (part, 1));

        Refused(await WithAsync(s => s.AddLineAsync(draft.Id, new RequisitionLineInput(part, 4), Viewer)), "requisition.line_duplicate");
    }

    [Fact]
    public async Task An_archived_part_cannot_be_added()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        await ArchiveAsync(part);
        var draft = await DraftAsync(Viewer);

        Refused(await WithAsync(s => s.AddLineAsync(draft.Id, new RequisitionLineInput(part, 1), Viewer)), "requisition.part_archived");
    }

    [Fact]
    public async Task A_part_that_does_not_exist_is_refused()
    {
        await UsersAsync();
        var draft = await DraftAsync(Viewer);

        Refused(await WithAsync(s => s.AddLineAsync(draft.Id, new RequisitionLineInput(9999, 1), Viewer)), "requisition.product_missing");
    }

    [Fact]
    public async Task A_line_can_be_changed_and_removed_and_removing_twice_is_fine()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var draft = await DraftAsync(Viewer, "Why", (part, 1));
        var lineId = draft.Lines.Single().Id;

        var updated = await WithAsync(s => s.UpdateLineAsync(draft.Id, lineId, 9, "  urgent  ", Viewer));
        Ok(updated);
        Assert.Equal(9, updated.Value.QuantityRequested);
        Assert.Equal("urgent", updated.Value.Note);

        Ok(await WithAsync(s => s.RemoveLineAsync(draft.Id, lineId, Viewer)));
        Ok(await WithAsync(s => s.RemoveLineAsync(draft.Id, lineId, Viewer)));
        Assert.Empty((await WithAsync(s => s.GetAsync(draft.Id, Viewer)))!.Lines);
    }

    [Fact]
    public async Task Updating_a_line_of_another_requisition_is_refused()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var mine = await DraftAsync(Viewer, "Why", (part, 1));
        var theirs = await DraftAsync(OtherViewer, "Why", (part, 1));

        Refused(await WithAsync(s => s.UpdateLineAsync(mine.Id, theirs.Lines.Single().Id, 5, null, Viewer)), "requisition.line_missing");
    }

    [Fact]
    public async Task Detail_lines_show_on_hand_and_on_order_for_each_part()
    {
        await UsersAsync();
        var part = await PartAsync("A", onHand: 7);
        var supplier = await NewSupplierAsync();
        await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 5));

        var draft = await DraftAsync(Viewer, "Why", (part, 1));

        var line = draft.Lines.Single();
        Assert.Equal(7, line.OnHand);
        Assert.Equal(5, line.OnOrder);
    }

    [Fact]
    public async Task The_lookup_gives_availability_and_flags_parts_already_on_the_requisition()
    {
        await UsersAsync();
        var a = await PartAsync("A-1", onHand: 3);
        var b = await PartAsync("A-2");
        var archived = await PartAsync("A-3");
        await ArchiveAsync(archived);
        var draft = await DraftAsync(Viewer, "Why", (a, 1));

        var found = await WithAsync(s => s.LookupProductsAsync(draft.Id, "A-", Viewer));

        Assert.Equal(2, found.Count);
        Assert.True(found.Single(p => p.ProductId == a).AlreadyOnRequisition);
        Assert.Equal(3, found.Single(p => p.ProductId == a).OnHand);
        Assert.False(found.Single(p => p.ProductId == b).AlreadyOnRequisition);
    }

    [Fact]
    public async Task The_lookup_says_nothing_about_someone_elses_requisition()
    {
        await UsersAsync();
        await PartAsync("A-1");
        var theirs = await DraftAsync(OtherViewer);

        Assert.Empty(await WithAsync(s => s.LookupProductsAsync(theirs.Id, "A-", Viewer)));
    }

    // ---- transitions -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_can_raise_and_submit_a_requisition()
    {
        await UsersAsync();
        var part = await PartAsync("A");

        var submitted = await SubmittedAsync(Viewer, part);

        Assert.Equal(RequisitionStatus.Submitted, submitted.Status);
        Assert.Equal(Clock.UtcNow, submitted.SubmittedAt);
        Assert.Equal("Vi Ewer", submitted.SubmittedByName);
        Assert.Contains(Recorded.Events, e => e.Type == RequisitionEventType.Submitted && e.UserId == _viewerId);
    }

    [Fact]
    public async Task Submit_needs_a_reason_and_a_line_and_reports_both()
    {
        await UsersAsync();
        var draft = await DraftAsync(Viewer, reason: null);

        var result = await WithAsync(s => s.SubmitAsync(draft.Id, draft.RowVersion, Viewer));

        Refused(result, "requisition.reason_required");
        Refused(result, "requisition.no_lines");
    }

    [Fact]
    public async Task A_blank_reason_does_not_count()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var draft = await DraftAsync(Viewer, "   ", (part, 1));

        Refused(await WithAsync(s => s.SubmitAsync(draft.Id, draft.RowVersion, Viewer)), "requisition.reason_required");
    }

    [Fact]
    public async Task Submit_is_refused_when_a_part_was_archived_while_in_draft()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var draft = await DraftAsync(Viewer, "Why", (part, 1));
        await ArchiveAsync(part);

        Refused(await WithAsync(s => s.SubmitAsync(draft.Id, draft.RowVersion, Viewer)), "requisition.part_archived");
    }

    [Fact]
    public async Task A_part_archived_after_submission_stays_on_the_requisition_and_is_shown_as_archived()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var submitted = await SubmittedAsync(Viewer, part);
        await ArchiveAsync(part);

        var detail = (await WithAsync(s => s.GetAsync(submitted.Id, Viewer)))!;

        Assert.False(detail.Lines.Single().ProductIsActive);
    }

    [Fact]
    public async Task A_submitted_requisition_cannot_be_edited_until_withdrawn()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var other = await PartAsync("B");
        var submitted = await SubmittedAsync(Viewer, part);
        var lineId = submitted.Lines.Single().Id;

        Refused(await WithAsync(s => s.UpdateHeaderAsync(submitted.Id, submitted.RowVersion, new RequisitionHeaderInput(null, "Changed"), Viewer)), "requisition.locked");
        Refused(await WithAsync(s => s.AddLineAsync(submitted.Id, new RequisitionLineInput(other, 1), Viewer)), "requisition.locked");
        Refused(await WithAsync(s => s.UpdateLineAsync(submitted.Id, lineId, 9, null, Viewer)), "requisition.locked");
        Refused(await WithAsync(s => s.RemoveLineAsync(submitted.Id, lineId, Viewer)), "requisition.locked");
        Refused(await WithAsync(s => s.DeleteDraftAsync(submitted.Id, submitted.RowVersion, Viewer)), "requisition.locked");
        Refused(await WithAsync(s => s.SubmitAsync(submitted.Id, submitted.RowVersion, Viewer)), "requisition.locked");

        var withdrawn = await WithAsync(s => s.WithdrawAsync(submitted.Id, submitted.RowVersion, Viewer));
        Ok(withdrawn);
        Ok(await WithAsync(s => s.UpdateHeaderAsync(submitted.Id, withdrawn.Value.RowVersion, new RequisitionHeaderInput(null, "Changed"), Viewer)));
    }

    [Fact]
    public async Task Withdrawing_returns_it_to_draft_and_clears_the_submission()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var submitted = await SubmittedAsync(Viewer, part);

        var withdrawn = await WithAsync(s => s.WithdrawAsync(submitted.Id, submitted.RowVersion, Viewer));

        Ok(withdrawn);
        Assert.Equal(RequisitionStatus.Draft, withdrawn.Value.Status);
        Assert.Null(withdrawn.Value.SubmittedAt);
        Assert.Null(withdrawn.Value.SubmittedByName);
        Assert.Contains(Recorded.Events, e => e.Type == RequisitionEventType.Withdrawn);
    }

    [Fact]
    public async Task A_draft_cannot_be_withdrawn()
    {
        await UsersAsync();
        var draft = await DraftAsync(Viewer);

        Refused(await WithAsync(s => s.WithdrawAsync(draft.Id, draft.RowVersion, Viewer)), "requisition.not_submitted");
    }

    [Theory]
    [InlineData(RequisitionStatus.Approved)]
    [InlineData(RequisitionStatus.Rejected)]
    [InlineData(RequisitionStatus.Cancelled)]
    public async Task A_decided_requisition_cannot_be_withdrawn_edited_or_deleted(RequisitionStatus status)
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var submitted = await SubmittedAsync(Viewer, part);
        await SetStatusAsync(submitted.Id, status);

        Refused(await WithAsync(s => s.WithdrawAsync(submitted.Id, null, Viewer)), "requisition.not_submitted");
        Refused(await WithAsync(s => s.UpdateHeaderAsync(submitted.Id, submitted.RowVersion, new RequisitionHeaderInput(null, "x"), Viewer)), "requisition.locked");
        Refused(await WithAsync(s => s.DeleteDraftAsync(submitted.Id, null, Viewer)), "requisition.locked");

        var detail = (await WithAsync(s => s.GetAsync(submitted.Id, Viewer)))!;
        Assert.False(detail.CanEdit);
        Assert.False(detail.CanWithdraw);
        Assert.False(detail.CanDelete);
    }

    [Fact]
    public async Task A_draft_can_be_deleted_with_its_lines_and_deleting_twice_is_fine()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var draft = await DraftAsync(Viewer, "Why", (part, 1));

        Ok(await WithAsync(s => s.DeleteDraftAsync(draft.Id, draft.RowVersion, Viewer)));
        Ok(await WithAsync(s => s.DeleteDraftAsync(draft.Id, draft.RowVersion, Viewer)));

        await using var read = Db.CreateContext();
        Assert.Empty(await read.Requisitions.ToListAsync());
        Assert.Empty(await read.RequisitionLines.ToListAsync());
    }

    // ---- concurrency -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stale_header_edit_is_reported_as_a_conflict()
    {
        await UsersAsync();
        var draft = await DraftAsync(Viewer);
        Ok(await WithAsync(s => s.UpdateHeaderAsync(draft.Id, draft.RowVersion, new RequisitionHeaderInput(null, "First"), Viewer)));

        Refused(await WithAsync(s => s.UpdateHeaderAsync(draft.Id, draft.RowVersion, new RequisitionHeaderInput(null, "Second"), Viewer)), "requisition.conflict");

        Assert.Equal("First", (await WithAsync(s => s.GetAsync(draft.Id, Viewer)))!.Reason);
    }

    [Fact]
    public async Task Two_saves_loaded_at_the_same_version_conflict_in_the_database()
    {
        await UsersAsync();
        var draft = await DraftAsync(Viewer);

        await using var first = Db.CreateContext();
        await using var second = Db.CreateContext();
        var a = await first.Requisitions.SingleAsync(r => r.Id == draft.Id);
        var b = await second.Requisitions.SingleAsync(r => r.Id == draft.Id);
        first.Entry(a).Property(r => r.RowVersion).CurrentValue = a.RowVersion + 1;
        await first.SaveChangesAsync();

        second.Entry(b).Property(r => r.RowVersion).CurrentValue = b.RowVersion + 1;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync());
    }

    [Fact]
    public async Task Changing_lines_moves_the_row_version_so_a_stale_submit_is_refused()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var other = await PartAsync("B");
        var draft = await DraftAsync(Viewer, "Why", (part, 1));

        Ok(await WithAsync(s => s.AddLineAsync(draft.Id, new RequisitionLineInput(other, 2), Viewer)));

        Refused(await WithAsync(s => s.SubmitAsync(draft.Id, draft.RowVersion, Viewer)), "requisition.conflict");
    }

    // ---- ownership ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_cannot_see_or_change_someone_elses_requisition()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var theirs = await DraftAsync(OtherViewer, "Why", (part, 1));

        Assert.Null(await WithAsync(s => s.GetAsync(theirs.Id, Viewer)));
        Refused(await WithAsync(s => s.UpdateHeaderAsync(theirs.Id, theirs.RowVersion, new RequisitionHeaderInput(null, "x"), Viewer)), "requisition.not_found");
        Refused(await WithAsync(s => s.SubmitAsync(theirs.Id, theirs.RowVersion, Viewer)), "requisition.not_found");
        Refused(await WithAsync(s => s.DeleteDraftAsync(theirs.Id, theirs.RowVersion, Viewer)), "requisition.not_found");
        Refused(await WithAsync(s => s.RemoveLineAsync(theirs.Id, theirs.Lines.Single().Id, Viewer)), "requisition.not_found");
    }

    [Fact]
    public async Task Staff_can_read_someone_elses_requisition_but_not_change_it()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var theirs = await DraftAsync(Viewer, "Why", (part, 1));

        var seen = await WithAsync(s => s.GetAsync(theirs.Id, StaffActor));

        Assert.NotNull(seen);
        Assert.False(seen.CanChange);
        Assert.False(seen.CanEdit);
        Refused(await WithAsync(s => s.UpdateHeaderAsync(theirs.Id, theirs.RowVersion, new RequisitionHeaderInput(null, "x"), StaffActor)), "requisition.not_owner");
        Refused(await WithAsync(s => s.SubmitAsync(theirs.Id, theirs.RowVersion, StaffActor)), "requisition.not_owner");
        Refused(await WithAsync(s => s.DeleteDraftAsync(theirs.Id, theirs.RowVersion, StaffActor)), "requisition.not_owner");
    }

    [Fact]
    public async Task Staff_cannot_withdraw_someone_elses_requisition()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var theirs = await SubmittedAsync(Viewer, part);

        Refused(await WithAsync(s => s.WithdrawAsync(theirs.Id, theirs.RowVersion, StaffActor)), "requisition.not_owner");
    }

    [Fact]
    public async Task An_admin_can_edit_submit_and_withdraw_someone_elses_and_is_recorded_as_submitter()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var theirs = await DraftAsync(Viewer, "Why", (part, 1));

        var edited = await WithAsync(s => s.UpdateHeaderAsync(theirs.Id, theirs.RowVersion, new RequisitionHeaderInput(null, "Admin fixed it"), Admin));
        Ok(edited);
        Assert.True(edited.Value.CanEdit);

        var submitted = await WithAsync(s => s.SubmitAsync(theirs.Id, edited.Value.RowVersion, Admin));
        Ok(submitted);
        Assert.Equal("Ad Min", submitted.Value.SubmittedByName);
        Assert.Equal(_viewerId, submitted.Value.RequestedById);

        Ok(await WithAsync(s => s.WithdrawAsync(theirs.Id, submitted.Value.RowVersion, Admin)));
    }

    // ---- list --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_viewer_only_ever_gets_their_own_even_when_asking_for_all()
    {
        await UsersAsync();
        var mine = await DraftAsync(Viewer);
        await DraftAsync(OtherViewer);

        var page = await WithAsync(s => s.SearchAsync(new RequisitionListQuery { Tab = RequisitionTab.All, RequesterId = _otherId }, Viewer));

        Assert.Equal(mine.Id, Assert.Single(page.Items).Id);
        Assert.Empty(await WithAsync(s => s.GetRequestersAsync(Viewer)));
    }

    [Fact]
    public async Task Staff_get_their_own_on_mine_and_everyones_on_all()
    {
        await UsersAsync();
        await DraftAsync(Viewer);
        await DraftAsync(OtherViewer);
        var own = await DraftAsync(StaffActor);

        var mine = await WithAsync(s => s.SearchAsync(new RequisitionListQuery(), StaffActor));
        var all = await WithAsync(s => s.SearchAsync(new RequisitionListQuery { Tab = RequisitionTab.All }, StaffActor));
        var filtered = await WithAsync(s => s.SearchAsync(new RequisitionListQuery { Tab = RequisitionTab.All, RequesterId = _viewerId }, StaffActor));

        Assert.Equal(own.Id, Assert.Single(mine.Items).Id);
        Assert.Equal(3, all.TotalCount);
        Assert.Equal(_viewerId, Assert.Single(filtered.Items).RequestedById);
        Assert.Equal(3, (await WithAsync(s => s.GetRequestersAsync(StaffActor))).Count);
    }

    [Fact]
    public async Task Search_matches_number_sku_and_part_name_and_treats_wildcards_literally()
    {
        await UsersAsync();
        var bolt = await PartAsync("BOLT-9");
        var nut = await PartAsync("NUT-1");
        var first = await DraftAsync(Viewer, "Why", (bolt, 1));
        var second = await DraftAsync(Viewer, "Why", (nut, 1));

        async Task<int> Found(string term) => Assert.Single((await WithAsync(s => s.SearchAsync(new RequisitionListQuery { Search = term }, Viewer))).Items).Id;

        Assert.Equal(second.Id, await Found("0002"));
        Assert.Equal(first.Id, await Found("bolt"));
        Assert.Equal(second.Id, await Found("Part NUT"));
        Assert.Empty((await WithAsync(s => s.SearchAsync(new RequisitionListQuery { Search = "%" }, Viewer))).Items);
    }

    [Fact]
    public async Task Filters_by_status_and_creation_date()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var submitted = await SubmittedAsync(Viewer, part);
        var draft = await DraftAsync(Viewer);
        var early = DateTime.UtcNow.AddDays(-3);
        await using (var context = Db.CreateContext())
        {
            await context.Requisitions.Where(r => r.Id == submitted.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.CreatedAt, early));
        }

        var byStatus = await WithAsync(s => s.SearchAsync(new RequisitionListQuery { Status = RequisitionStatus.Submitted }, Viewer));
        var byDate = await WithAsync(s => s.SearchAsync(new RequisitionListQuery { FromUtc = early.AddDays(1) }, Viewer));
        var upTo = await WithAsync(s => s.SearchAsync(new RequisitionListQuery { ToUtcExclusive = early.AddDays(1) }, Viewer));

        Assert.Equal(submitted.Id, Assert.Single(byStatus.Items).Id);
        Assert.Equal(draft.Id, Assert.Single(byDate.Items).Id);
        Assert.Equal(submitted.Id, Assert.Single(upTo.Items).Id);
    }

    [Fact]
    public async Task The_list_is_newest_first_and_counts_lines()
    {
        await UsersAsync();
        var part = await PartAsync("A");
        var older = await DraftAsync(Viewer, "Why", (part, 1));
        var newer = await DraftAsync(Viewer);

        var page = await WithAsync(s => s.SearchAsync(new RequisitionListQuery(), Viewer));

        Assert.Equal([newer.Id, older.Id], page.Items.Select(i => i.Id));
        Assert.Equal(1, page.Items[1].LineCount);
    }

    // ---- no side effects ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_whole_lifecycle_leaves_stock_and_purchase_orders_alone()
    {
        await UsersAsync();
        var part = await PartAsync("A", onHand: 5);

        async Task<(int OnHand, int Movements, int Orders, int OrderLines)> Snapshot()
        {
            await using var read = Db.CreateContext();
            return ((await read.Products.SingleAsync(p => p.Id == part)).QuantityOnHand,
                await read.StockTransactions.CountAsync(),
                await read.PurchaseOrders.CountAsync(),
                await read.PurchaseOrderLines.CountAsync());
        }

        var before = await Snapshot();
        var submitted = await SubmittedAsync(Viewer, part);
        var withdrawn = await WithAsync(s => s.WithdrawAsync(submitted.Id, submitted.RowVersion, Viewer));
        Ok(withdrawn);
        Ok(await WithAsync(s => s.DeleteDraftAsync(submitted.Id, withdrawn.Value.RowVersion, Viewer)));

        Assert.Equal(before, await Snapshot());
    }
}
