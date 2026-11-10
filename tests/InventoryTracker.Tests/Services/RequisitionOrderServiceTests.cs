using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-13-2: sending approved requisition lines to purchase orders, releasing them, and cancelling approved requisitions.</summary>
public sealed class RequisitionOrderServiceTests : PurchaseOrderTestBase
{
    private RecordingRequisitionEvents Recorded { get; } = new();

    private string _requesterId = null!;

    private RequisitionActor Requester => new(_requesterId);

    private RequisitionActor Manager => new(UserId, CanSeeAll: true, CanApprove: true, CanOrder: true);

    private RequisitionActor Staff => new(UserId, CanSeeAll: true, CanOrder: true);

    private async Task UsersAsync()
    {
        await using var context = Db.CreateContext();
        var requester = new ApplicationUser { UserName = "asker", DisplayName = "Ann Asker" };
        context.Users.Add(requester);
        await context.SaveChangesAsync();
        _requesterId = requester.Id;
    }

    private async Task<T> WithAsync<T>(Func<RequisitionService, RequisitionOrderService, PurchaseOrderService, Task<T>> action)
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            var publisher = new RequisitionEventPublisher([Recorded], NullLogger<RequisitionEventPublisher>.Instance);
            return await action(
                new RequisitionService(context, new ProductService(context), Settings, Clock, publisher),
                new RequisitionOrderService(context, orders, Clock, publisher),
                orders);
        }
    }

    private Task<Result<IReadOnlyList<ConversionOutcome>>> ConvertAsync(RequisitionActor actor, params ConversionGroup[] groups)
        => WithAsync((_, c, _) => c.ConvertAsync(groups, actor));

    private Task<ToOrderView?> ToOrderAsync(int? purchaseOrderId = null) => WithAsync((_, c, _) => c.GetToOrderAsync(purchaseOrderId));

    private Task<RequisitionDetail?> RequisitionAsync(int id) => WithAsync((r, _, _) => r.GetAsync(id, Manager));

    private static void Ok(Result result) => Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));

    private static void Refused(Result result, string code)
    {
        Assert.False(result.Succeeded, "Expected the service to refuse.");
        Assert.Contains(result.Errors, e => e.Code == code);
    }

    /// <summary>A part, preferred at <paramref name="supplierId"/> when given.</summary>
    private async Task<int> PartAsync(string sku, int? supplierId = null, int minOrder = 1)
    {
        var id = (await CreateProductAsync(Input(sku: sku, name: "Part " + sku))).Id;
        if (supplierId is { } supplier)
        {
            await using var context = Db.CreateContext();
            context.ProductSuppliers.Add(new ProductSupplier { ProductId = id, SupplierId = supplier, UnitCostCents = 250, MinOrderQuantity = minOrder, IsPreferred = true });
            await context.SaveChangesAsync();
        }

        return id;
    }

    /// <summary>A requisition raised by the requester and approved by the Manager; quantities are requested, approved.</summary>
    private async Task<RequisitionDetail> ApprovedAsync(params (int ProductId, int Requested, int Approved)[] lines)
    {
        var created = await WithAsync((r, _, _) => r.CreateDraftAsync(new RequisitionHeaderInput(null, "Line stopped"), Requester));
        Ok(created);
        foreach (var line in lines)
        {
            Ok(await WithAsync((r, _, _) => r.AddLineAsync(created.Value.Id, new RequisitionLineInput(line.ProductId, line.Requested), Requester)));
        }

        var draft = (await WithAsync((r, _, _) => r.GetAsync(created.Value.Id, Requester)))!;
        var submitted = await WithAsync((r, _, _) => r.SubmitAsync(draft.Id, draft.RowVersion, Requester));
        Ok(submitted);
        var decisions = submitted.Value.Lines.Select((l, i) => lines[i].Approved == 0
            ? new RequisitionLineDecisionInput(l.Id, RequisitionDecision.Rejected, null, "No")
            : new RequisitionLineDecisionInput(l.Id, RequisitionDecision.Approved, lines[i].Approved, lines[i].Approved < lines[i].Requested ? "Less" : null)).ToList();
        var decided = await WithAsync((r, _, _) => r.DecideAsync(submitted.Value.Id, submitted.Value.RowVersion, decisions, Manager));
        Ok(decided);
        return decided.Value;
    }

    private async Task<int> CountAsync<TEntity>() where TEntity : class
    {
        await using var context = Db.CreateContext();
        return await context.Set<TEntity>().CountAsync();
    }

    [Fact]
    public async Task The_list_groups_approved_unordered_lines_by_preferred_supplier_with_availability()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync("Acme");
        var bolt = await PartAsync("BOLT", acme, minOrder: 10);
        var loose = await PartAsync("LOOSE");
        var archived = await NewSupplierAsync("Gone", archived: true);
        var orphan = await PartAsync("ORPHAN", archived);
        var draft = await DraftAsync(acme, null, new PurchaseOrderLineInput(bolt, 4));
        var requisition = await ApprovedAsync((bolt, 5, 5), (loose, 2, 2), (orphan, 1, 1));

        var view = (await ToOrderAsync())!;

        var acmeGroup = Assert.Single(view.Groups, g => g.SupplierId == acme);
        var line = Assert.Single(acmeGroup.Lines);
        Assert.Equal(requisition.Number, line.RequisitionNumber);
        Assert.Equal(4, line.InDraft);
        Assert.True(line.BelowMinimum);
        Assert.Equal(draft.Id, Assert.Single(acmeGroup.Drafts).PurchaseOrderId);
        var unassigned = Assert.Single(view.Groups, g => g.SupplierId is null);
        Assert.Equal(["LOOSE", "ORPHAN"], unassigned.Lines.Select(l => l.Sku).Order());
        Assert.Contains(view.Suppliers, s => s.Id == acme);
        Assert.DoesNotContain(view.Suppliers, s => s.Id == archived);
    }

    [Fact]
    public async Task Rejected_and_unapproved_lines_are_not_listed()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var a = await PartAsync("A", acme);
        var b = await PartAsync("B", acme);
        await ApprovedAsync((a, 3, 3), (b, 2, 0));

        var view = (await ToOrderAsync())!;

        Assert.Equal(["A"], view.Groups.SelectMany(g => g.Lines).Select(l => l.Sku));
    }

    [Fact]
    public async Task Converting_to_a_new_draft_orders_the_approved_quantity_and_links_the_line()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var part = await PartAsync("A", acme);
        var requisition = await ApprovedAsync((part, 5, 3));

        var result = await ConvertAsync(Staff, new ConversionGroup(acme, null, [requisition.Lines[0].Id]));

        Ok(result);
        var outcome = Assert.Single(result.Value);
        Assert.True(outcome.Created);
        var order = await GetOrderAsync(outcome.PurchaseOrderId);
        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Equal(3, Assert.Single(order.Lines).QuantityOrdered);
        Assert.Equal(250, order.Lines[0].UnitCostCents);

        var detail = (await RequisitionAsync(requisition.Id))!;
        Assert.Equal(order.Number, detail.Lines[0].OrderedOn!.Number);
        Assert.Equal(RequisitionOrderState.Ordered, detail.OrderState);
        Assert.Empty((await ToOrderAsync())!.Groups);
        Assert.Equal(0, await CountAsync<StockTransaction>());
    }

    [Fact]
    public async Task Converting_onto_an_existing_draft_adds_to_a_line_already_there()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var a = await PartAsync("A", acme);
        var b = await PartAsync("B", acme);
        var draft = await DraftAsync(acme, null, new PurchaseOrderLineInput(a, 2));
        var requisition = await ApprovedAsync((a, 3, 3), (b, 4, 4));

        var result = await ConvertAsync(Staff, new ConversionGroup(acme, draft.Id, [.. requisition.Lines.Select(l => l.Id)]));

        Ok(result);
        Assert.False(Assert.Single(result.Value).Created);
        var order = await GetOrderAsync(draft.Id);
        Assert.Equal(5, order.Lines.Single(l => l.ProductId == a).QuantityOrdered);
        Assert.Equal(4, order.Lines.Single(l => l.ProductId == b).QuantityOrdered);
    }

    [Fact]
    public async Task Two_requisitions_for_the_same_part_share_one_order_line()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var part = await PartAsync("A", acme);
        var first = await ApprovedAsync((part, 2, 2));
        var second = await ApprovedAsync((part, 3, 3));

        var result = await ConvertAsync(Staff, new ConversionGroup(acme, null, [first.Lines[0].Id]), new ConversionGroup(acme, null, [second.Lines[0].Id]));

        Ok(result);
        var order = await GetOrderAsync(Assert.Single(result.Value).PurchaseOrderId);
        Assert.Equal(5, Assert.Single(order.Lines).QuantityOrdered);
        var sources = await WithAsync((_, c, _) => c.GetSourcesForOrderAsync(order.Id));
        Assert.Equal([first.Number, second.Number], sources.Select(s => s.RequisitionNumber));
        Assert.Equal(2, await CountAsync<RequisitionLineOrder>());
    }

    [Fact]
    public async Task A_line_cannot_be_ordered_twice()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var part = await PartAsync("A", acme);
        var requisition = await ApprovedAsync((part, 2, 2));
        Ok(await ConvertAsync(Staff, new ConversionGroup(acme, null, [requisition.Lines[0].Id])));

        var again = await ConvertAsync(Staff, new ConversionGroup(acme, null, [requisition.Lines[0].Id]));

        Refused(again, "requisition.already_ordered");
        Assert.Equal(RequisitionOrderFields.Line(requisition.Lines[0].Id), again.Errors[0].Field);
        Assert.Equal(1, await CountAsync<PurchaseOrder>());
    }

    [Fact]
    public async Task Only_a_draft_of_the_same_supplier_can_receive_lines()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync("Acme");
        var other = await NewSupplierAsync("Other");
        var part = await PartAsync("A", acme);
        var submitted = await SubmittedAsync(acme, new PurchaseOrderLineInput(part, 1));
        var otherDraft = await DraftAsync(other);
        var requisition = await ApprovedAsync((part, 2, 2));
        var lineId = requisition.Lines[0].Id;

        Refused(await ConvertAsync(Staff, new ConversionGroup(acme, submitted.Id, [lineId])), "requisition.order_not_draft");
        Refused(await ConvertAsync(Staff, new ConversionGroup(acme, otherDraft.Id, [lineId])), "requisition.order_other_supplier");
        Assert.Equal(0, await CountAsync<RequisitionLineOrder>());
    }

    [Fact]
    public async Task Archived_parts_and_suppliers_cannot_be_ordered()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync("Acme");
        var gone = await NewSupplierAsync("Gone", archived: true);
        var part = await PartAsync("A", acme);
        var requisition = await ApprovedAsync((part, 2, 2));

        Refused(await ConvertAsync(Staff, new ConversionGroup(gone, null, [requisition.Lines[0].Id])), "requisition.supplier_archived");

        await ArchiveProductAsync(part);
        Assert.NotNull((await ToOrderAsync())!.Groups.Single().Lines.Single().Blocked);
        Refused(await ConvertAsync(Staff, new ConversionGroup(acme, null, [requisition.Lines[0].Id])), "requisition.part_archived");
        Assert.Equal(0, await CountAsync<PurchaseOrder>());
    }

    [Fact]
    public async Task One_bad_group_leaves_nothing_behind()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync("Acme");
        var other = await NewSupplierAsync("Other");
        var a = await PartAsync("A", acme);
        var b = await PartAsync("B", other);
        var requisition = await ApprovedAsync((a, 2, 2), (b, 2, 2));
        var submitted = await SubmittedAsync(other, new PurchaseOrderLineInput(b, 1));
        var ordersBefore = await CountAsync<PurchaseOrder>();

        var result = await ConvertAsync(Staff, new ConversionGroup(acme, null, [requisition.Lines[0].Id]), new ConversionGroup(other, submitted.Id, [requisition.Lines[1].Id]));

        Refused(result, "requisition.order_not_draft");
        Assert.Equal(ordersBefore, await CountAsync<PurchaseOrder>());
        Assert.Equal(0, await CountAsync<RequisitionLineOrder>());
    }

    [Fact]
    public async Task Removing_the_line_deleting_the_draft_or_cancelling_the_order_releases_the_requisition_line()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var part = await PartAsync("A", acme);
        var requisition = await ApprovedAsync((part, 2, 2));
        var lineId = requisition.Lines[0].Id;
        async Task<int> SendAsync()
        {
            var result = await ConvertAsync(Staff, new ConversionGroup(acme, null, [lineId]));
            Ok(result);
            return result.Value[0].PurchaseOrderId;
        }

        async Task AssertReleasedAsync() => Assert.Equal(lineId, (await ToOrderAsync())!.Groups.Single().Lines.Single().RequisitionLineId);

        var first = await SendAsync();
        var firstLine = (await GetOrderAsync(first)).Lines[0].Id;
        Ok(await RunAsync(o => o.RemoveLineAsync(first, firstLine)));
        await AssertReleasedAsync();

        var second = await SendAsync();
        Ok(await RunAsync(o => o.DeleteDraftAsync(second, Actor)));
        await AssertReleasedAsync();

        var third = await SendAsync();
        Ok(await RunAsync(async o => await o.SubmitAsync(third, Actor)));
        Ok(await RunAsync(o => o.CancelAsync(third, Actor)));
        await AssertReleasedAsync();
        Assert.Equal(PurchaseOrderStatus.Cancelled, (await GetOrderAsync(third)).Status);

        // A released line can be ordered again.
        await SendAsync();
        Assert.Equal(RequisitionOrderState.Ordered, (await RequisitionAsync(requisition.Id))!.OrderState);
    }

    [Fact]
    public async Task Someone_who_cannot_edit_cannot_order()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var part = await PartAsync("A", acme);
        var requisition = await ApprovedAsync((part, 2, 2));

        Refused(await ConvertAsync(Requester, new ConversionGroup(acme, null, [requisition.Lines[0].Id])), "requisition.not_buyer");
    }

    [Fact]
    public async Task Partly_ordered_and_ordered_are_derived_and_filterable()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var a = await PartAsync("A", acme);
        var b = await PartAsync("B", acme);
        var partly = await ApprovedAsync((a, 2, 2), (b, 2, 2));
        var whole = await ApprovedAsync((a, 1, 1));
        var none = await ApprovedAsync((b, 1, 1));
        Ok(await ConvertAsync(Staff, new ConversionGroup(acme, null, [partly.Lines[0].Id, whole.Lines[0].Id])));

        async Task<List<int>> FilterAsync(RequisitionOrderState state)
            => [.. (await WithAsync((r, _, _) => r.SearchAsync(new RequisitionListQuery { Tab = RequisitionTab.All, Ordering = state }, Manager))).Items.Select(i => i.Id)];

        Assert.Equal([partly.Id], await FilterAsync(RequisitionOrderState.PartlyOrdered));
        Assert.Equal([whole.Id], await FilterAsync(RequisitionOrderState.Ordered));
        Assert.Equal([none.Id], await FilterAsync(RequisitionOrderState.NotOrdered));
        Assert.Equal(RequisitionOrderState.PartlyOrdered, (await RequisitionAsync(partly.Id))!.OrderState);
        var row = (await WithAsync((r, _, _) => r.SearchAsync(new RequisitionListQuery { Tab = RequisitionTab.All }, Manager))).Items.Single(i => i.Id == partly.Id);
        Assert.Equal(RequisitionOrderState.PartlyOrdered, row.OrderState);
    }

    [Fact]
    public async Task Conversion_is_published_once_per_requisition()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var part = await PartAsync("A", acme);
        var other = await PartAsync("B", acme);
        var requisition = await ApprovedAsync((part, 2, 2), (other, 1, 1));
        Recorded.Events.Clear();

        Ok(await ConvertAsync(Staff, new ConversionGroup(acme, null, [.. requisition.Lines.Select(l => l.Id)])));

        var published = Assert.Single(Recorded.Events);
        Assert.Equal(RequisitionEventType.Converted, published.Type);
        Assert.Equal(requisition.Id, published.RequisitionId);
    }

    [Fact]
    public async Task An_approved_requisition_can_be_cancelled_only_while_nothing_is_ordered()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync();
        var part = await PartAsync("A", acme);
        var ordered = await ApprovedAsync((part, 2, 2));
        var idle = await ApprovedAsync((part, 1, 1));
        Ok(await ConvertAsync(Staff, new ConversionGroup(acme, null, [ordered.Lines[0].Id])));
        var orderedNow = (await RequisitionAsync(ordered.Id))!;
        Assert.False(orderedNow.CanCancelApproved);

        Refused(await WithAsync((r, _, _) => r.CancelApprovedAsync(ordered.Id, orderedNow.RowVersion, "Not needed", Manager)), "requisition.has_orders");
        Refused(await WithAsync((r, _, _) => r.CancelApprovedAsync(idle.Id, idle.RowVersion, "Not needed", Staff)), "requisition.not_approver");
        Refused(await WithAsync((r, _, _) => r.CancelApprovedAsync(idle.Id, idle.RowVersion, "", Manager)), "requisition.cancel_reason_required");

        var cancelled = await WithAsync((r, _, _) => r.CancelApprovedAsync(idle.Id, idle.RowVersion, "Not needed", Manager));

        Ok(cancelled);
        Assert.Equal(RequisitionStatus.Cancelled, cancelled.Value.Status);
        Assert.DoesNotContain((await ToOrderAsync())!.Groups.SelectMany(g => g.Lines), l => l.RequisitionId == idle.Id);
    }

    [Fact]
    public async Task The_list_for_one_draft_shows_only_its_supplier_and_unassigned_lines()
    {
        await UsersAsync();
        var acme = await NewSupplierAsync("Acme");
        var other = await NewSupplierAsync("Other");
        var a = await PartAsync("A", acme);
        var b = await PartAsync("B", other);
        var c = await PartAsync("C");
        await ApprovedAsync((a, 1, 1), (b, 1, 1), (c, 1, 1));
        var draft = await DraftAsync(acme);
        var submitted = await SubmittedAsync(acme, new PurchaseOrderLineInput(a, 1));

        var view = (await ToOrderAsync(draft.Id))!;

        Assert.Equal(draft.Number, view.ForOrder!.Number);
        Assert.Equal(["A", "C"], view.Groups.SelectMany(g => g.Lines).Select(l => l.Sku).Order());
        Assert.Null(await ToOrderAsync(submitted.Id));
    }

    private async Task ArchiveProductAsync(int productId)
    {
        await using var context = Db.CreateContext();
        await context.Products.Where(p => p.Id == productId).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false));
    }
}

/// <summary>Real file database, one connection per request: two people sending the same line at once.</summary>
public sealed class RequisitionOrderConcurrencyTests : IAsyncLifetime
{
    private string _path = null!;
    private string _userId = null!;
    private int _supplierId;
    private long _lineId;

    private readonly TestClock _clock = new(new DateTime(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc));

    public async Task InitializeAsync()
    {
        _path = Path.Combine(Path.GetTempPath(), $"inventory-reqpo-{Guid.NewGuid():N}.db");
        await using var context = NewContext();
        await context.Database.MigrateAsync();
        var user = new ApplicationUser { UserName = "buyer", DisplayName = "Bea Buyer" };
        var product = new Product { Sku = "RACE-1", Name = "Racing widget" };
        var supplier = new Supplier { Name = "Acme" };
        context.AddRange(user, product, supplier);
        await context.SaveChangesAsync();
        var requisition = new Requisition
        {
            Number = "REQ-2026-0001",
            RequestedById = user.Id,
            Status = RequisitionStatus.Approved,
            DecidedAt = _clock.UtcNow,
            Lines = [new RequisitionLine { ProductId = product.Id, QuantityRequested = 2 }],
        };
        context.Requisitions.Add(requisition);
        await context.SaveChangesAsync();
        context.RequisitionLineDecisions.Add(new RequisitionLineDecision
        {
            RequisitionLineId = requisition.Lines[0].Id,
            Decision = RequisitionDecision.Approved,
            QuantityApproved = 2,
            DecidedById = user.Id,
            DecidedAt = _clock.UtcNow,
        });
        await context.SaveChangesAsync();
        _userId = user.Id;
        _supplierId = supplier.Id;
        _lineId = requisition.Lines[0].Id;
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

    [Fact]
    public async Task Converting_the_same_line_in_parallel_creates_one_link()
    {
        var actor = new RequisitionActor(_userId, CanSeeAll: true, CanOrder: true);
        var gate = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await gate.Task;
            await using var context = NewContext();
            var settings = new FakeSettings();
            var orders = new PurchaseOrderService(
                context, new StockService(context, settings, _clock, NullLogger<StockService>.Instance), settings, _clock,
                new PurchaseOrderEventPublisher([], NullLogger<PurchaseOrderEventPublisher>.Instance));
            var service = new RequisitionOrderService(context, orders, _clock, new RequisitionEventPublisher([], NullLogger<RequisitionEventPublisher>.Instance));
            return await service.ConvertAsync([new ConversionGroup(_supplierId, null, [_lineId])], actor);
        })).ToArray();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, results.Count(r => r.Succeeded));
        Assert.All(results.Where(r => !r.Succeeded), r => Assert.Contains(r.Errors, e => e.Code == "requisition.already_ordered"));
        await using var read = NewContext();
        Assert.Equal(1, await read.RequisitionLineOrders.CountAsync());
        Assert.Equal(1, await read.PurchaseOrders.CountAsync());
    }
}
