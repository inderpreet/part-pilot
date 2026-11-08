using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-05-2: numbering, drafts, lines, transitions, receiving, reversing receipts and queries.</summary>
public sealed class PurchaseOrderServiceTests : PurchaseOrderTestBase
{
    private async Task<int> ProductAsync(string sku, long costCents = 0) => (await CreateProductAsync(Input(sku: sku, name: "Product " + sku, costCents: costCents))).Id;

    // ---- state machine -----------------------------------------------------------------------------------------

    public static TheoryData<PurchaseOrderStatus, PurchaseOrderStatus, bool> TransitionTable()
    {
        const PurchaseOrderStatus d = PurchaseOrderStatus.Draft;
        const PurchaseOrderStatus s = PurchaseOrderStatus.Submitted;
        const PurchaseOrderStatus p = PurchaseOrderStatus.PartiallyReceived;
        const PurchaseOrderStatus r = PurchaseOrderStatus.Received;
        const PurchaseOrderStatus c = PurchaseOrderStatus.Cancelled;

        var allowed = new HashSet<(PurchaseOrderStatus, PurchaseOrderStatus)>
        {
            (d, s), (d, c),
            (s, p), (s, r), (s, c),
            (p, r), (p, s),
            (r, p), (r, s),
        };

        var data = new TheoryData<PurchaseOrderStatus, PurchaseOrderStatus, bool>();
        foreach (var from in Enum.GetValues<PurchaseOrderStatus>())
        {
            foreach (var to in Enum.GetValues<PurchaseOrderStatus>())
            {
                data.Add(from, to, allowed.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(TransitionTable))]
    public void Every_pair_of_statuses_is_allowed_or_refused_as_specified(PurchaseOrderStatus from, PurchaseOrderStatus to, bool expected)
        => Assert.Equal(expected, PurchaseOrderRules.CanTransition(from, to));

    [Fact]
    public void Action_predicates_follow_the_status()
    {
        Assert.Equal(
            [PurchaseOrderStatus.Draft],
            Enum.GetValues<PurchaseOrderStatus>().Where(PurchaseOrderRules.CanEditLines));
        Assert.Equal(
            [PurchaseOrderStatus.Submitted, PurchaseOrderStatus.PartiallyReceived],
            Enum.GetValues<PurchaseOrderStatus>().Where(PurchaseOrderRules.CanReceive));
        Assert.Equal(
            [PurchaseOrderStatus.Draft, PurchaseOrderStatus.Submitted],
            Enum.GetValues<PurchaseOrderStatus>().Where(PurchaseOrderRules.CanCancel));
        Assert.Equal(
            [PurchaseOrderStatus.PartiallyReceived],
            Enum.GetValues<PurchaseOrderStatus>().Where(PurchaseOrderRules.CanCloseShort));
        Assert.Equal(
            [PurchaseOrderStatus.Draft],
            Enum.GetValues<PurchaseOrderStatus>().Where(PurchaseOrderRules.CanDelete));
        Assert.Equal(
            [PurchaseOrderStatus.Draft, PurchaseOrderStatus.Submitted, PurchaseOrderStatus.PartiallyReceived],
            Enum.GetValues<PurchaseOrderStatus>().Where(PurchaseOrderRules.CanEditHeader));
    }

    [Fact]
    public void Status_is_derived_from_the_received_quantities()
    {
        Assert.Equal(PurchaseOrderStatus.Submitted, PurchaseOrderRules.StatusFor([(5, 0), (3, 0)]));
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderRules.StatusFor([(5, 5), (3, 0)]));
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, PurchaseOrderRules.StatusFor([(5, 2), (3, 0)]));
        Assert.Equal(PurchaseOrderStatus.Received, PurchaseOrderRules.StatusFor([(5, 5), (3, 4)]));
        Assert.Equal(PurchaseOrderStatus.Submitted, PurchaseOrderRules.StatusFor([]));
    }

    // ---- numbering ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Numbers_are_sequential_within_a_year_and_restart_each_year()
    {
        var supplier = await NewSupplierAsync();

        var first = await DraftAsync(supplier, new DateOnly(2026, 2, 1));
        var second = await DraftAsync(supplier, new DateOnly(2026, 11, 30));
        var next = await DraftAsync(supplier, new DateOnly(2027, 1, 2));
        var backToFirstYear = await DraftAsync(supplier, new DateOnly(2026, 12, 31));

        Assert.Equal("PO-2026-0001", first.Number);
        Assert.Equal("PO-2026-0002", second.Number);
        Assert.Equal("PO-2027-0001", next.Number);
        Assert.Equal("PO-2026-0003", backToFirstYear.Number);
    }

    [Fact]
    public async Task The_prefix_comes_from_the_setting()
    {
        Settings.PurchaseOrderPrefix = "ORD/";
        var supplier = await NewSupplierAsync();

        Assert.Equal("ORD/2026-0001", (await DraftAsync(supplier)).Number);
    }

    [Fact]
    public async Task A_deleted_drafts_number_is_never_issued_again()
    {
        var supplier = await NewSupplierAsync();
        var first = await DraftAsync(supplier);
        Assert.True((await RunAsync(o => o.DeleteDraftAsync(first.Id, Actor))).Succeeded);

        var second = await DraftAsync(supplier);

        Assert.Equal("PO-2026-0002", second.Number);
    }

    [Fact]
    public async Task NextNumber_issues_numbers_on_its_own_and_inside_a_transaction()
    {
        var number = await QueryAsync(o => o.NextNumberAsync(2030));
        Assert.Equal("PO-2030-0001", number);

        var (context, orders) = NewOrders();
        await using (context)
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            Assert.Equal("PO-2030-0002", await orders.NextNumberAsync(2030));
            await transaction.RollbackAsync();
        }

        // The rolled-back number goes back with the transaction.
        Assert.Equal("PO-2030-0002", await QueryAsync(o => o.NextNumberAsync(2030)));
    }

    // ---- creating drafts ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_draft_records_supplier_dates_creator_and_defaults()
    {
        var supplier = await NewSupplierAsync("Globex");

        var order = await DraftAsync(supplier, new DateOnly(2026, 3, 9));

        Assert.Equal(PurchaseOrderStatus.Draft, order.Status);
        Assert.Equal("Globex", order.SupplierName);
        Assert.Equal(new DateOnly(2026, 3, 9), order.OrderDate);
        Assert.Null(order.ExpectedDate);
        Assert.Null(order.SubmittedAt);
        Assert.Equal(UserId, order.CreatedById);
        Assert.Equal("Mo Ver", order.CreatedByName);
        Assert.Empty(order.Lines);
        Assert.Equal(0, order.TotalCents);
        Assert.True(order.CanEditLines);
        Assert.False(order.CanSubmit);
        Assert.False(order.CanReceive);
        Assert.True(order.CanDelete);
        Assert.Equal(PurchaseOrderEventType.Created, Assert.Single(Events.Events).Type);
    }

    [Fact]
    public async Task The_default_order_date_is_today()
    {
        var supplier = await NewSupplierAsync();
        var (context, orders) = NewOrders();
        await using (context)
        {
            var result = await orders.CreateDraftAsync(supplier, Actor);
            Assert.Equal(DateOnly.FromDateTime(Clock.UtcNow.ToLocalTime()), result.Value.OrderDate);
        }
    }

    [Fact]
    public async Task Line_costs_default_to_the_supplier_price_then_the_product_cost_and_an_explicit_cost_wins()
    {
        var supplier = await NewSupplierAsync();
        var linked = await ProductAsync("L-1", costCents: 900);
        var unlinked = await ProductAsync("L-2", costCents: 700);
        var zeroLink = await ProductAsync("L-3", costCents: 650);
        var explicitCost = await ProductAsync("L-4", costCents: 500);
        await LinkAsync(linked, supplier, 450);
        await LinkAsync(zeroLink, supplier, 0);
        await LinkAsync(explicitCost, supplier, 450);

        var order = await DraftAsync(
            supplier,
            null,
            new PurchaseOrderLineInput(linked, 2),
            new PurchaseOrderLineInput(unlinked, 3),
            new PurchaseOrderLineInput(zeroLink, 4),
            new PurchaseOrderLineInput(explicitCost, 5, UnitCostCents: 120));

        Assert.Equal([450L, 700L, 650L, 120L], order.Lines.Select(l => l.UnitCostCents));
        Assert.Equal((2 * 450) + (3 * 700) + (4 * 650) + (5 * 120), order.TotalCents);
        Assert.Equal([2, 3, 4, 5], order.Lines.Select(l => l.QuantityOrdered));
    }

    [Fact]
    public async Task A_draft_cannot_be_created_for_a_missing_or_archived_supplier()
    {
        var archived = await NewSupplierAsync("Old Co", archived: true);

        var (context, orders) = NewOrders();
        await using (context)
        {
            AssertFailed(await orders.CreateDraftAsync(archived, Actor), "po.supplier_archived", "SupplierId");
            AssertFailed(await orders.CreateDraftAsync(9999, Actor), "po.supplier_missing", "SupplierId");
        }

        Assert.Equal(0, await Db.CreateContext().PurchaseOrders.CountAsync());
    }

    [Fact]
    public async Task Initial_lines_are_validated_with_line_paths_and_nothing_is_created_on_failure()
    {
        var supplier = await NewSupplierAsync();
        var good = await ProductAsync("G-1");
        var archived = await ProductAsync("G-2");
        await ArchiveAsync(archived);

        var (context, orders) = NewOrders();
        await using (context)
        {
            var bad = await orders.CreateDraftAsync(
                supplier,
                Actor,
                [new PurchaseOrderLineInput(good, 0), new PurchaseOrderLineInput(good, 1, -5)],
                Jan5);
            Assert.False(bad.Succeeded);
            Assert.Contains(bad.Errors, e => e.Code == "po.quantity_positive" && e.Field == "Lines[0].Quantity");
            Assert.Contains(bad.Errors, e => e.Code == "po.cost_negative" && e.Field == "Lines[1].UnitCost");
            Assert.Contains(bad.Errors, e => e.Code == "po.line_duplicate");

            var arch = await orders.CreateDraftAsync(supplier, Actor, [new PurchaseOrderLineInput(archived, 1)], Jan5);
            Assert.Contains(arch.Errors, e => e.Code == "po.product_archived" && e.Field == "Lines[0].ProductId");

            var missing = await orders.CreateDraftAsync(supplier, Actor, [new PurchaseOrderLineInput(9999, 1)], Jan5);
            Assert.Contains(missing.Errors, e => e.Code == "po.product_missing");
        }

        Assert.Equal(0, await Db.CreateContext().PurchaseOrders.CountAsync());
        Assert.Equal(0, await Db.CreateContext().DocumentSequences.CountAsync());
    }

    // ---- header ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_draft_header_can_change_everything_while_it_has_no_lines()
    {
        var one = await NewSupplierAsync("One");
        var two = await NewSupplierAsync("Two");
        var draft = await DraftAsync(one);

        var result = await RunAsync(async o =>
        {
            var r = await o.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(two, new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 20), "  Call first  "));
            return r.Succeeded ? Result.Success() : Result.Failure([.. r.Errors]);
        });

        Assert.True(result.Succeeded, Messages(result));
        var saved = await GetOrderAsync(draft.Id);
        Assert.Equal(two, saved.SupplierId);
        Assert.Equal(new DateOnly(2026, 2, 2), saved.OrderDate);
        Assert.Equal(new DateOnly(2026, 2, 20), saved.ExpectedDate);
        Assert.Equal("Call first", saved.Notes);
        Assert.True(saved.RowVersion > draft.RowVersion);
    }

    [Fact]
    public async Task The_supplier_is_locked_once_there_are_lines_and_must_be_active()
    {
        var one = await NewSupplierAsync("One");
        var two = await NewSupplierAsync("Two");
        var old = await NewSupplierAsync("Old", archived: true);
        var product = await ProductAsync("H-1");
        var empty = await DraftAsync(one);
        var withLine = await DraftAsync(one, null, new PurchaseOrderLineInput(product, 1));

        var (context, orders) = NewOrders();
        await using (context)
        {
            var locked = await orders.UpdateHeaderAsync(withLine.Id, withLine.RowVersion, new PurchaseOrderHeaderInput(two, Jan5, null, null));
            AssertFailed(locked, "po.supplier_locked", "SupplierId");

            var archived = await orders.UpdateHeaderAsync(empty.Id, empty.RowVersion, new PurchaseOrderHeaderInput(old, Jan5, null, null));
            AssertFailed(archived, "po.supplier_archived", "SupplierId");

            var missing = await orders.UpdateHeaderAsync(empty.Id, empty.RowVersion, new PurchaseOrderHeaderInput(9999, Jan5, null, null));
            AssertFailed(missing, "po.supplier_missing", "SupplierId");
        }
    }

    [Fact]
    public async Task A_submitted_order_only_accepts_a_new_expected_date_and_notes()
    {
        var one = await NewSupplierAsync("One");
        var two = await NewSupplierAsync("Two");
        var product = await ProductAsync("H-2");
        var order = await SubmittedAsync(one, new PurchaseOrderLineInput(product, 1));

        var (context, orders) = NewOrders();
        await using (context)
        {
            var ok = await orders.UpdateHeaderAsync(order.Id, order.RowVersion, new PurchaseOrderHeaderInput(one, order.OrderDate, new DateOnly(2026, 4, 1), "Delayed"));
            Assert.True(ok.Succeeded, Messages(ok));
            Assert.Equal(new DateOnly(2026, 4, 1), ok.Value.ExpectedDate);

            AssertFailed(
                await orders.UpdateHeaderAsync(order.Id, ok.Value.RowVersion, new PurchaseOrderHeaderInput(two, order.OrderDate, null, null)),
                "po.header_locked");
            AssertFailed(
                await orders.UpdateHeaderAsync(order.Id, ok.Value.RowVersion, new PurchaseOrderHeaderInput(one, order.OrderDate.AddDays(1), null, null)),
                "po.header_locked");
        }
    }

    [Fact]
    public async Task A_closed_order_header_cannot_change()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("H-3");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 1));
        Assert.True((await RunAsync(o => o.CancelAsync(order.Id, Actor))).Succeeded);
        var cancelled = await GetOrderAsync(order.Id);

        var (context, orders) = NewOrders();
        await using (context)
        {
            AssertFailed(
                await orders.UpdateHeaderAsync(order.Id, cancelled.RowVersion, new PurchaseOrderHeaderInput(supplier, order.OrderDate, null, "x")),
                "po.closed");
        }
    }

    [Fact]
    public async Task Header_validation_and_stale_versions_are_reported()
    {
        var supplier = await NewSupplierAsync();
        var draft = await DraftAsync(supplier);

        var (context, orders) = NewOrders();
        await using (context)
        {
            AssertFailed(
                await orders.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(supplier, Jan5, Jan5.AddDays(-1), null)),
                "po.expected_before_order",
                "ExpectedDate");
            AssertFailed(
                await orders.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(supplier, Jan5, null, new string('n', 2001))),
                "po.notes_too_long",
                "Notes");
            AssertFailed(
                await orders.UpdateHeaderAsync(draft.Id, draft.RowVersion + 5, new PurchaseOrderHeaderInput(supplier, Jan5, null, null)),
                "po.conflict");
            AssertFailed(
                await orders.UpdateHeaderAsync(9999, 1, new PurchaseOrderHeaderInput(supplier, Jan5, null, null)),
                "po.not_found");

            // The same date is fine.
            Assert.True((await orders.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(supplier, Jan5, Jan5, null))).Succeeded);
        }
    }

    [Fact]
    public async Task Two_header_edits_from_the_same_version_conflict()
    {
        var supplier = await NewSupplierAsync();
        var draft = await DraftAsync(supplier);

        var (c1, o1) = NewOrders();
        var (c2, o2) = NewOrders();
        await using (c1)
        await using (c2)
        {
            Assert.True((await o1.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(supplier, Jan5, null, "first"))).Succeeded);
            AssertFailed(await o2.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(supplier, Jan5, null, "second")), "po.conflict");
        }

        Assert.Equal("first", (await GetOrderAsync(draft.Id)).Notes);
    }

    // ---- lines -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Lines_are_added_updated_and_removed_on_a_draft()
    {
        var supplier = await NewSupplierAsync();
        var a = await ProductAsync("A-1", costCents: 300);
        var b = await ProductAsync("B-1", costCents: 100);
        await LinkAsync(a, supplier, 250, supplierSku: "ACME-A");
        var draft = await DraftAsync(supplier);

        var (context, orders) = NewOrders();
        await using (context)
        {
            var lineA = await orders.AddLineAsync(draft.Id, a, 4);
            var lineB = await orders.AddLineAsync(draft.Id, b, 2, unitCostCents: 175);
            Assert.True(lineA.Succeeded, Messages(lineA));
            Assert.Equal(250L, lineA.Value.UnitCostCents);
            Assert.Equal("ACME-A", lineA.Value.SupplierSku);
            Assert.Equal(175L, lineB.Value.UnitCostCents);
            Assert.Null(lineB.Value.SupplierSku);

            var updated = await orders.UpdateLineAsync(draft.Id, lineA.Value.Id, 10, 260);
            Assert.True(updated.Succeeded, Messages(updated));
            Assert.Equal(10, updated.Value.QuantityOrdered);
            Assert.Equal(260L, updated.Value.UnitCostCents);

            Assert.True((await orders.RemoveLineAsync(draft.Id, lineB.Value.Id)).Succeeded);
            Assert.True((await orders.RemoveLineAsync(draft.Id, lineB.Value.Id)).Succeeded, "Removing a line that is already gone is fine.");
        }

        var saved = await GetOrderAsync(draft.Id);
        var line = Assert.Single(saved.Lines);
        Assert.Equal(a, line.ProductId);
        Assert.Equal(2600L, saved.TotalCents);
    }

    [Fact]
    public async Task Adding_a_line_validates_quantity_cost_product_and_duplicates()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("V-1");
        var archived = await ProductAsync("V-2");
        await ArchiveAsync(archived);
        var draft = await DraftAsync(supplier);

        var (context, orders) = NewOrders();
        await using (context)
        {
            AssertFailed(await orders.AddLineAsync(draft.Id, product, 0), "po.quantity_positive", "Quantity");
            AssertFailed(await orders.AddLineAsync(draft.Id, product, -3), "po.quantity_positive", "Quantity");
            AssertFailed(await orders.AddLineAsync(draft.Id, product, StockService.MaxQuantity + 1), "po.quantity_too_large", "Quantity");
            AssertFailed(await orders.AddLineAsync(draft.Id, product, 1, -1), "po.cost_negative", "UnitCost");
            AssertFailed(await orders.AddLineAsync(draft.Id, product, 1, PurchaseOrderService.MaxUnitCostCents + 1), "po.cost_too_large", "UnitCost");
            AssertFailed(await orders.AddLineAsync(draft.Id, archived, 1), "po.product_archived", "ProductId");
            AssertFailed(await orders.AddLineAsync(draft.Id, 9999, 1), "po.product_missing", "ProductId");
            AssertFailed(await orders.AddLineAsync(9999, product, 1), "po.not_found");

            Assert.True((await orders.AddLineAsync(draft.Id, product, 1)).Succeeded);
            AssertFailed(await orders.AddLineAsync(draft.Id, product, 1), "po.line_duplicate", "ProductId");
        }

        Assert.Single((await GetOrderAsync(draft.Id)).Lines);
    }

    [Fact]
    public async Task Lines_cannot_change_after_the_order_is_submitted()
    {
        var supplier = await NewSupplierAsync();
        var a = await ProductAsync("S-1");
        var b = await ProductAsync("S-2");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(a, 5));
        var lineId = order.Lines[0].Id;

        var (context, orders) = NewOrders();
        await using (context)
        {
            AssertFailed(await orders.AddLineAsync(order.Id, b, 1), "po.lines_locked", "Lines");
            AssertFailed(await orders.UpdateLineAsync(order.Id, lineId, 9, 1), "po.lines_locked", "Lines");
            AssertFailed(await orders.RemoveLineAsync(order.Id, lineId), "po.lines_locked", "Lines");
        }

        var saved = await GetOrderAsync(order.Id);
        Assert.Equal(5, Assert.Single(saved.Lines).QuantityOrdered);
    }

    [Fact]
    public async Task A_line_of_another_order_is_not_found()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("X-1");
        var mine = await DraftAsync(supplier);
        var other = await DraftAsync(supplier, null, new PurchaseOrderLineInput(product, 3));

        var (context, orders) = NewOrders();
        await using (context)
        {
            AssertFailed(await orders.UpdateLineAsync(mine.Id, other.Lines[0].Id, 9, 1), "po.line_missing");
            Assert.True((await orders.RemoveLineAsync(mine.Id, other.Lines[0].Id)).Succeeded);
        }

        Assert.Single((await GetOrderAsync(other.Id)).Lines);
    }

    [Fact]
    public async Task Line_changes_do_not_bump_the_header_version_so_an_open_header_form_stays_valid()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("RV-1");
        var draft = await DraftAsync(supplier);

        var (context, orders) = NewOrders();
        await using (context)
        {
            Assert.True((await orders.AddLineAsync(draft.Id, product, 2)).Succeeded);
            Assert.True((await orders.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(supplier, Jan5, null, "ok"))).Succeeded);
        }
    }

    // ---- transitions -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Submit_needs_a_line_and_stamps_the_time()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("T-1");
        var empty = await DraftAsync(supplier);


        var (context, orders) = NewOrders();
        await using (context)
        {
            var none = await orders.SubmitAsync(empty.Id, Actor);
            AssertFailed(none, "po.no_lines", "Lines");

            Assert.True((await orders.AddLineAsync(empty.Id, product, 1)).Succeeded);
            var ok = await orders.SubmitAsync(empty.Id, Actor);
            Assert.True(ok.Succeeded, Messages(ok));
            Assert.Equal(PurchaseOrderStatus.Submitted, ok.Value.Status);
            Assert.Equal(Clock.UtcNow, ok.Value.SubmittedAt);
            Assert.False(ok.Value.CanEditLines);
            Assert.True(ok.Value.CanReceive);
        }

        Assert.Contains(Events.Events, e => e.Type == PurchaseOrderEventType.Submitted && e.UserId == UserId);
    }

    [Fact]
    public async Task Submit_fills_the_expected_date_from_the_longest_lead_time_unless_one_was_set()
    {
        var supplier = await NewSupplierAsync();
        var fast = await ProductAsync("LT-1");
        var slow = await ProductAsync("LT-2");
        var none = await ProductAsync("LT-3");
        await LinkAsync(fast, supplier, 100, leadTimeDays: 3);
        await LinkAsync(slow, supplier, 100, leadTimeDays: 10);

        var derived = await SubmittedAsync(supplier, new PurchaseOrderLineInput(fast, 1), new PurchaseOrderLineInput(slow, 1), new PurchaseOrderLineInput(none, 1));
        Assert.Equal(Jan5.AddDays(10), derived.ExpectedDate);

        var noLead = await SubmittedAsync(supplier, new PurchaseOrderLineInput(none, 1));
        Assert.Null(noLead.ExpectedDate);

        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(slow, 1));
        var (context, orders) = NewOrders();
        await using (context)
        {
            Assert.True((await orders.UpdateHeaderAsync(draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(supplier, Jan5, Jan5.AddDays(2), null))).Succeeded);
            var kept = await orders.SubmitAsync(draft.Id, Actor);
            Assert.Equal(Jan5.AddDays(2), kept.Value.ExpectedDate);
        }
    }

    [Fact]
    public async Task Submit_refuses_an_archived_supplier_or_product()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("SA-1");
        var other = await ProductAsync("SA-2");
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(product, 1));
        var second = await DraftAsync(supplier, null, new PurchaseOrderLineInput(other, 1));

        await ArchiveAsync(product);
        await using (var context = Db.CreateContext())
        {
            await context.Suppliers.Where(s => s.Id == supplier).ExecuteUpdateAsync(s => s.SetProperty(x => x.IsActive, false));
        }

        var (c, orders) = NewOrders();
        await using (c)
        {
            var result = await orders.SubmitAsync(draft.Id, Actor);
            Assert.Contains(result.Errors, e => e.Code == "po.supplier_archived");
            Assert.Contains(result.Errors, e => e.Code == "po.product_archived");
            Assert.Equal(PurchaseOrderStatus.Draft, (await orders.GetAsync(draft.Id))!.Status);
            Assert.Contains((await orders.SubmitAsync(second.Id, Actor)).Errors, e => e.Code == "po.supplier_archived");
        }
    }

    [Fact]
    public async Task Submit_twice_or_with_a_stale_page_is_refused()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("SS-1");
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(product, 1));

        var (context, orders) = NewOrders();
        await using (context)
        {
            AssertFailed(await orders.SubmitAsync(draft.Id, Actor, rowVersion: draft.RowVersion + 3), "po.conflict");
            Assert.True((await orders.SubmitAsync(draft.Id, Actor, rowVersion: draft.RowVersion)).Succeeded);
            AssertFailed(await orders.SubmitAsync(draft.Id, Actor), "po.invalid_transition");
            AssertFailed(await orders.SubmitAsync(9999, Actor), "po.not_found");
        }
    }

    [Fact]
    public async Task Cancel_works_for_drafts_and_submitted_orders_only()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("C-1");
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(product, 4));
        var submitted = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 4));
        var partial = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 4));
        Assert.True((await ReceiveAsync(partial.Id, [new ReceiveLineInput(partial.Lines[0].Id, 1)])).Succeeded);
        var received = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 1));
        Assert.True((await ReceiveAsync(received.Id, [new ReceiveLineInput(received.Lines[0].Id, 1)])).Succeeded);

        Assert.True((await RunAsync(o => o.CancelAsync(draft.Id, Actor))).Succeeded);
        Assert.True((await RunAsync(o => o.CancelAsync(submitted.Id, Actor))).Succeeded);
        var again = await RunAsync(o => o.CancelAsync(submitted.Id, Actor));
        AssertFailed(again, "po.invalid_transition");
        var partlyReceived = await RunAsync(o => o.CancelAsync(partial.Id, Actor));
        AssertFailed(partlyReceived, "po.invalid_transition");
        Assert.Contains("Close it short", partlyReceived.Errors[0].Message);
        AssertFailed(await RunAsync(o => o.CancelAsync(received.Id, Actor)), "po.invalid_transition");
        AssertFailed(await RunAsync(o => o.CancelAsync(9999, Actor)), "po.not_found");

        var cancelled = await GetOrderAsync(submitted.Id);
        Assert.Equal(PurchaseOrderStatus.Cancelled, cancelled.Status);
        Assert.Equal(Clock.UtcNow, cancelled.ClosedAt);
        Assert.Equal(0, cancelled.OutstandingCents);
        Assert.Equal(2, Events.Events.Count(e => e.Type == PurchaseOrderEventType.Cancelled));
    }

    [Fact]
    public async Task A_stale_cancel_is_refused()
    {
        var supplier = await NewSupplierAsync();
        var draft = await DraftAsync(supplier);

        AssertFailed(await RunAsync(o => o.CancelAsync(draft.Id, Actor, rowVersion: draft.RowVersion + 1)), "po.conflict");
        Assert.Equal(PurchaseOrderStatus.Draft, (await GetOrderAsync(draft.Id)).Status);
    }

    [Fact]
    public async Task Only_a_draft_can_be_deleted_and_its_lines_go_with_it()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("D-1");
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(product, 4));
        var submitted = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 4));

        AssertFailed(await RunAsync(o => o.DeleteDraftAsync(submitted.Id, Actor)), "po.delete_not_draft");
        AssertFailed(await RunAsync(o => o.DeleteDraftAsync(draft.Id, Actor, rowVersion: draft.RowVersion + 1)), "po.conflict");

        Assert.True((await RunAsync(o => o.DeleteDraftAsync(draft.Id, Actor, draft.RowVersion))).Succeeded);
        Assert.True((await RunAsync(o => o.DeleteDraftAsync(draft.Id, Actor))).Succeeded, "A second delete is a no-op.");

        await using var read = Db.CreateContext();
        Assert.Null(await read.PurchaseOrders.FindAsync(draft.Id));
        Assert.Equal(1, await read.PurchaseOrderLines.CountAsync());
        Assert.Equal(1, Events.Events.Count(e => e.Type == PurchaseOrderEventType.DraftDeleted));
    }

    [Fact]
    public async Task Close_short_finishes_a_partly_received_order()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("CS-1", costCents: 100);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 10));

        AssertFailed(await RunAsync(o => o.CloseShortAsync(order.Id, Actor)), "po.invalid_transition");

        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 4)])).Succeeded);
        Assert.True((await RunAsync(o => o.CloseShortAsync(order.Id, Actor))).Succeeded);

        var closed = await GetOrderAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.Received, closed.Status);
        Assert.Equal(Clock.UtcNow, closed.ClosedAt);
        Assert.Equal(0, closed.OutstandingCents);
        Assert.False(closed.CanReceive);
        AssertFailed(await RunAsync(o => o.CloseShortAsync(order.Id, Actor)), "po.invalid_transition");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 1)]), "po.not_receivable");
        Assert.Contains(Events.Events, e => e.Type == PurchaseOrderEventType.ClosedShort);
    }

    // ---- receiving ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receiving_some_lines_makes_it_partial_and_the_rest_completes_it()
    {
        var supplier = await NewSupplierAsync();
        var a = await ProductAsync("RC-A", costCents: 100);
        var b = await ProductAsync("RC-B", costCents: 200);
        var c = await ProductAsync("RC-C", costCents: 300);
        var order = await SubmittedAsync(
            supplier,
            new PurchaseOrderLineInput(a, 5),
            new PurchaseOrderLineInput(b, 3),
            new PurchaseOrderLineInput(c, 4));
        var (la, lb, lc) = (order.Lines[0].Id, order.Lines[1].Id, order.Lines[2].Id);

        var first = await ReceiveAsync(order.Id, [new ReceiveLineInput(la, 5), new ReceiveLineInput(lb, 3), new ReceiveLineInput(lc, 0)], reference: "DN-100", note: "Pallet 1");

        Assert.True(first.Succeeded, Messages(first));
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, first.Value.Status);
        Assert.Equal(2, first.Value.LinesReceived);
        Assert.Equal(8, first.Value.UnitsReceived);
        Assert.Equal(5, await OnHandAsync(a));
        Assert.Equal(3, await OnHandAsync(b));
        Assert.Equal(0, await OnHandAsync(c));
        var partial = await GetOrderAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, partial.Status);
        Assert.Equal([5, 3, 0], partial.Lines.Select(l => l.QuantityReceived));
        Assert.Equal(4 * 300, partial.OutstandingCents);
        Assert.Null(partial.ClosedAt);
        Assert.True(partial.CanCloseShort);

        var rest = await ReceiveAsync(order.Id, [new ReceiveLineInput(lc, 4)]);

        Assert.True(rest.Succeeded, Messages(rest));
        Assert.Equal(PurchaseOrderStatus.Received, rest.Value.Status);
        Assert.Equal(4, await OnHandAsync(c));
        var done = await GetOrderAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.Received, done.Status);
        Assert.Equal(Clock.UtcNow, done.ClosedAt);
        Assert.Equal(0, done.OutstandingCents);
        Assert.Equal(2, Events.Events.Count(e => e.Type == PurchaseOrderEventType.Received));

        foreach (var product in new[] { a, b, c })
        {
            await AssertConsistentAsync(product);
        }
    }

    [Fact]
    public async Task A_receipt_posts_receive_transactions_at_the_line_cost_linked_to_the_line()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("RT-1", costCents: 999);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 10, UnitCostCents: 250));

        await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 6)], reference: "  DN-7 ", note: " Left dock ");
        await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 1)]);

        var ledger = await LedgerAsync(product);
        Assert.Equal(2, ledger.Count);
        var row = ledger[0];
        Assert.Equal(StockTransactionType.Receive, row.Type);
        Assert.Equal(6, row.QuantityDelta);
        Assert.Equal(250L, row.UnitCostCents);
        Assert.Equal(order.Lines[0].Id, row.PurchaseOrderLineId);
        Assert.Equal("DN-7", row.Reference);
        Assert.Equal("Left dock", row.Note);
        Assert.Equal($"Received on {order.Number}", row.Reason);
        Assert.Equal(UserId, row.UserId);
        Assert.Equal(order.Number, ledger[1].Reference);
    }

    [Fact]
    public async Task Receive_input_is_validated()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("RV-1");
        var other = await ProductAsync("RV-2");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 5), new PurchaseOrderLineInput(other, 5));
        var (l1, l2) = (order.Lines[0].Id, order.Lines[1].Id);

        AssertFailed(await ReceiveAsync(order.Id, []), "po.nothing_to_receive", "Lines");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, 0), new ReceiveLineInput(l2, 0)]), "po.nothing_to_receive", "Lines");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, -1)]), "po.quantity_negative", "Lines[0].Quantity");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, StockService.MaxQuantity + 1)]), "po.quantity_too_large", "Lines[0].Quantity");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, 1), new ReceiveLineInput(l1, 1)]), "po.line_duplicate");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, 1), new ReceiveLineInput(987654, 1)]), "po.line_missing", "Lines[1].Quantity");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, 1)], reference: new string('r', 101)), "po.reference_too_long", "Reference");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, 1)], note: new string('n', 501)), "po.note_too_long", "Note");
        AssertFailed(await ReceiveAsync(9999, [new ReceiveLineInput(l1, 1)]), "po.not_found");
        AssertFailed(await ReceiveAsync(order.Id, [new ReceiveLineInput(l1, 1)], rowVersion: order.RowVersion + 9), "po.conflict");

        Assert.Empty(await LedgerAsync());
        Assert.All((await GetOrderAsync(order.Id)).Lines, l => Assert.Equal(0, l.QuantityReceived));
        Assert.DoesNotContain(Events.Events, e => e.Type == PurchaseOrderEventType.Received);
    }

    [Fact]
    public async Task Only_submitted_or_partly_received_orders_accept_goods()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("NR-1");
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(product, 5));
        var cancelled = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 5));
        await RunAsync(o => o.CancelAsync(cancelled.Id, Actor));

        var onDraft = await ReceiveAsync(draft.Id, [new ReceiveLineInput(draft.Lines[0].Id, 1)]);
        AssertFailed(onDraft, "po.not_receivable");
        Assert.Contains("draft", onDraft.Errors[0].Message);
        AssertFailed(await ReceiveAsync(cancelled.Id, [new ReceiveLineInput(cancelled.Lines[0].Id, 1)]), "po.not_receivable");
        Assert.Empty(await LedgerAsync());
    }

    [Fact]
    public async Task Receiving_more_than_outstanding_is_blocked_unless_an_admin_allows_it()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("OV-1", costCents: 10);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 5));
        var line = order.Lines[0].Id;

        var blocked = await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 6)]);
        AssertFailed(blocked, "po.over_receipt", "Lines[0].Quantity");
        Assert.Contains("Only 5 outstanding", blocked.Errors[0].Message);

        var staffOverride = await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 6)], allowOver: true);
        AssertFailed(staffOverride, "po.over_receipt_forbidden", "Lines[0].Quantity");
        Assert.Equal(0, await OnHandAsync(product));

        var admin = await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 6)], allowOver: true, actor: AdminActor);
        Assert.True(admin.Succeeded, Messages(admin));
        Assert.Equal(PurchaseOrderStatus.Received, admin.Value.Status);
        Assert.Equal(6, await OnHandAsync(product));

        var saved = await GetOrderAsync(order.Id);
        Assert.Equal(6, saved.Lines[0].QuantityReceived);
        Assert.Equal(0, saved.Lines[0].Outstanding);
        Assert.Equal(0, saved.OutstandingCents);
    }

    [Fact]
    public async Task Nothing_outstanding_is_reported_clearly_and_allow_over_is_not_needed_when_not_over()
    {
        var supplier = await NewSupplierAsync();
        var a = await ProductAsync("NO-1");
        var b = await ProductAsync("NO-2");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(a, 2), new PurchaseOrderLineInput(b, 2));
        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 2)], allowOver: true)).Succeeded);

        var again = await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 1)]);

        AssertFailed(again, "po.over_receipt", "Lines[0].Quantity");
        Assert.Equal("Nothing is outstanding on this line.", again.Errors[0].Message);
        Assert.Equal(2, await OnHandAsync(a));
    }

    [Fact]
    public async Task A_failing_stock_post_rolls_the_whole_receipt_back_and_maps_the_error_to_the_input_line()
    {
        var supplier = await NewSupplierAsync();
        var a = await ProductAsync("RB-A");
        var b = await ProductAsync("RB-B");
        var c = await ProductAsync("RB-C");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(a, 5), new PurchaseOrderLineInput(b, 5), new PurchaseOrderLineInput(c, 5));
        await ArchiveAsync(c);

        // Input line 0 is skipped (zero), so the stock service sees B as its line 0 and C as line 1.
        var result = await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 0), new ReceiveLineInput(order.Lines[1].Id, 5), new ReceiveLineInput(order.Lines[2].Id, 5)]);

        AssertFailed(result, "stock.product_archived", "Lines[2].Quantity");
        Assert.Empty(await LedgerAsync());
        Assert.Equal(0, await OnHandAsync(b));
        var saved = await GetOrderAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.Submitted, saved.Status);
        Assert.All(saved.Lines, l => Assert.Equal(0, l.QuantityReceived));
        Assert.DoesNotContain(Events.Events, e => e.Type == PurchaseOrderEventType.Received);
    }

    [Fact]
    public async Task A_stock_overflow_is_reported_on_the_line()
    {
        var supplier = await NewSupplierAsync();
        var product = await NewProductAsync("BIG-1", onHand: StockService.MaxQuantity - 1);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 5));

        var result = await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 5)]);

        AssertFailed(result, "stock.too_large", "Lines[0].Quantity");
        Assert.Equal(StockService.MaxQuantity - 1, await OnHandAsync(product));
        Assert.Equal(0, (await GetOrderAsync(order.Id)).Lines[0].QuantityReceived);
    }

    [Fact]
    public async Task A_stale_page_cannot_over_receive_because_outstanding_is_re_read_inside_the_transaction()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("ST-1");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 5));
        var line = order.Lines[0].Id;

        // Two receive screens opened at the same time, both showing 5 outstanding.
        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 4)])).Succeeded);
        var second = await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 4)]);

        AssertFailed(second, "po.over_receipt", "Lines[0].Quantity");
        Assert.Equal(4, await OnHandAsync(product));
    }

    [Fact]
    public async Task A_failing_event_handler_never_breaks_receiving()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("EV-1");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 2));
        Events.Throw = true;

        var result = await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 2)]);

        Assert.True(result.Succeeded);
        Assert.Equal(2, await OnHandAsync(product));
    }

    // ---- reversing receipts ------------------------------------------------------------------------------------

    private async Task<(PurchaseOrderDetail Order, long Transaction)> ReceivedAsync(int quantity = 5, int ordered = 5, string sku = "RR-1")
    {
        var supplier = await NewSupplierAsync("Supplier " + sku);
        var product = await ProductAsync(sku, costCents: 100);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, ordered));
        var received = await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, quantity)]);
        Assert.True(received.Succeeded, Messages(received));
        return (order, received.Value.Movements[0].TransactionId!.Value);
    }

    [Fact]
    public async Task Reversing_a_receipt_lowers_the_line_and_reopens_a_received_order()
    {
        var (order, receipt) = await ReceivedAsync();
        Assert.Equal(PurchaseOrderStatus.Received, (await GetOrderAsync(order.Id)).Status);

        var reversed = await ReverseReceiptAsync(receipt, "Wrong delivery");

        Assert.True(reversed.Succeeded, Messages(reversed));
        var saved = await GetOrderAsync(order.Id);
        Assert.Equal(0, saved.Lines[0].QuantityReceived);
        Assert.Equal(PurchaseOrderStatus.Submitted, saved.Status);
        Assert.Null(saved.ClosedAt);
        Assert.True(saved.CanReceive);
        var productId = saved.Lines[0].ProductId;
        Assert.Equal(0, await OnHandAsync(productId));
        await AssertConsistentAsync(productId);
        var ledger = await LedgerAsync(productId);
        Assert.Equal(StockTransactionType.Reversal, ledger[^1].Type);
        Assert.Equal(order.Lines[0].Id, ledger[^1].PurchaseOrderLineId);
        Assert.Equal("Wrong delivery", ledger[^1].Note);
        Assert.Contains(Events.Events, e => e.Type == PurchaseOrderEventType.ReceiptReversed);

        // And it can be received again.
        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 5)])).Succeeded);
    }

    [Fact]
    public async Task Reversing_one_of_two_receipts_leaves_the_order_partially_received()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("RR-2");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 10));
        var line = order.Lines[0].Id;
        var first = (await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 4)])).Value.Movements[0].TransactionId!.Value;
        var second = (await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 6)])).Value.Movements[0].TransactionId!.Value;
        Assert.Equal(PurchaseOrderStatus.Received, (await GetOrderAsync(order.Id)).Status);

        Assert.True((await ReverseReceiptAsync(second)).Succeeded);
        var afterSecond = await GetOrderAsync(order.Id);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, afterSecond.Status);
        Assert.Equal(4, afterSecond.Lines[0].QuantityReceived);

        Assert.True((await ReverseReceiptAsync(first)).Succeeded);
        Assert.Equal(PurchaseOrderStatus.Submitted, (await GetOrderAsync(order.Id)).Status);
        Assert.Equal(0, await OnHandAsync(product));
    }

    [Fact]
    public async Task The_generic_stock_reverse_refuses_order_receipts_and_the_order_reverse_refuses_other_rows()
    {
        var (_, receipt) = await ReceivedAsync(sku: "RR-3");
        var plain = await NewProductAsync("RR-3P", onHand: 4);
        var plainRow = (await LedgerAsync(plain))[0].Id;

        AssertFailed(await ReverseAsync(receipt), "stock.reverse_order_receipt");
        AssertFailed(await ReverseReceiptAsync(plainRow), "po.not_a_receipt");
        AssertFailed(await ReverseReceiptAsync(987654), "po.receipt_missing");
        Assert.Equal(4, await OnHandAsync(plain));
    }

    [Fact]
    public async Task A_receipt_can_be_reversed_only_once_and_a_reversal_is_not_a_receipt()
    {
        var (_, receipt) = await ReceivedAsync(sku: "RR-4");
        var reversal = (await ReverseReceiptAsync(receipt)).Value.TransactionId!.Value;

        AssertFailed(await ReverseReceiptAsync(receipt), "stock.already_reversed");
        AssertFailed(await ReverseReceiptAsync(reversal), "po.not_a_receipt");
    }

    [Fact]
    public async Task A_reversal_the_stock_cannot_cover_is_refused_and_changes_nothing()
    {
        var (order, receipt) = await ReceivedAsync(sku: "RR-5");
        var productId = order.Lines[0].ProductId;
        Assert.True((await PostAsync(Issue(productId, 3))).Succeeded);

        var result = await ReverseReceiptAsync(receipt);

        AssertFailed(result, "stock.insufficient");
        var saved = await GetOrderAsync(order.Id);
        Assert.Equal(5, saved.Lines[0].QuantityReceived);
        Assert.Equal(PurchaseOrderStatus.Received, saved.Status);
        Assert.Equal(2, await OnHandAsync(productId));
    }

    [Fact]
    public async Task A_too_long_reversal_note_is_rejected()
    {
        var (_, receipt) = await ReceivedAsync(sku: "RR-6");

        AssertFailed(await ReverseReceiptAsync(receipt, new string('n', 501)), "po.note_too_long", "Note");
    }

    [Fact]
    public async Task The_movement_item_hides_the_generic_reverse_for_order_receipts()
    {
        var (_, receipt) = await ReceivedAsync(sku: "RR-7");

        var (context, stock) = NewStock();
        await using (context)
        {
            var item = (await stock.GetAsync(receipt))!;
            Assert.False(item.CanReverse);
            Assert.NotNull(item.PurchaseOrderLineId);
        }
    }

    [Fact]
    public async Task Receipts_list_flags_what_can_still_be_reversed()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("RL-1");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 10));
        var line = order.Lines[0].Id;
        var first = (await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 4)], reference: "DN-1")).Value.Movements[0].TransactionId!.Value;
        await ReceiveAsync(order.Id, [new ReceiveLineInput(line, 3)], reference: "DN-2");
        await ReverseReceiptAsync(first);

        var receipts = await QueryAsync(o => o.GetReceiptsAsync(order.Id));

        Assert.Equal(3, receipts.Count);
        Assert.Equal([StockTransactionType.Reversal, StockTransactionType.Receive, StockTransactionType.Receive], receipts.Select(r => r.Type));
        Assert.Equal([false, true, false], receipts.Select(r => r.CanReverse));
        Assert.Equal([false, false, true], receipts.Select(r => r.IsReversed));
        Assert.Equal("DN-2", receipts[1].Reference);
        Assert.All(receipts, r => Assert.Equal(line, r.LineId));
        Assert.Empty(await QueryAsync(o => o.GetReceiptsAsync(9999)));

        await RunAsync(o => o.CloseShortAsync(order.Id, Actor));
        Assert.Contains(await QueryAsync(o => o.GetReceiptsAsync(order.Id)), r => r.CanReverse);
    }

    // ---- queries -----------------------------------------------------------------------------------------------

    private async Task<(int Draft, int Open, int Partial, int Received, int Cancelled, int Supplier, int Other)> SeedListAsync()
    {
        var acme = await NewSupplierAsync("Acme");
        var globex = await NewSupplierAsync("Globex");
        var product = await ProductAsync("LS-1", costCents: 100);

        var draft = await DraftAsync(acme, new DateOnly(2026, 1, 10), new PurchaseOrderLineInput(product, 2));
        var open = await SubmittedAsync(acme, new PurchaseOrderLineInput(product, 10, 200)); // order date Jan 5
        var partial = await SubmittedAsync(globex, new PurchaseOrderLineInput(product, 10, 300));
        await ReceiveAsync(partial.Id, [new ReceiveLineInput(partial.Lines[0].Id, 4)]);
        var received = await SubmittedAsync(globex, new PurchaseOrderLineInput(product, 1, 50));
        await ReceiveAsync(received.Id, [new ReceiveLineInput(received.Lines[0].Id, 1)]);
        var cancelled = await SubmittedAsync(acme, new PurchaseOrderLineInput(product, 1, 70));
        await RunAsync(o => o.CancelAsync(cancelled.Id, Actor));

        return (draft.Id, open.Id, partial.Id, received.Id, cancelled.Id, acme, globex);
    }

    [Fact]
    public async Task The_list_filters_by_status_tab()
    {
        var (draft, open, partial, received, cancelled, _, _) = await SeedListAsync();

        async Task<int[]> Ids(PurchaseOrderTab tab)
            => (await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { Tab = tab, Sort = "number" }))).Items.Select(i => i.Id).Order().ToArray();

        Assert.Equal(new[] { draft, open, partial, received, cancelled }.Order(), await Ids(PurchaseOrderTab.All));
        Assert.Equal(new[] { open, partial }.Order(), await Ids(PurchaseOrderTab.Open));
        Assert.Equal([draft], await Ids(PurchaseOrderTab.Draft));
        Assert.Equal([received], await Ids(PurchaseOrderTab.Received));
        Assert.Equal([cancelled], await Ids(PurchaseOrderTab.Cancelled));
    }

    [Fact]
    public async Task The_list_filters_by_supplier_date_range_and_number()
    {
        var (draft, open, _, _, cancelled, acme, _) = await SeedListAsync();

        var bySupplier = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { SupplierId = acme }));
        Assert.Equal(new[] { draft, open, cancelled }.Order(), bySupplier.Items.Select(i => i.Id).Order());

        var range = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { From = new DateOnly(2026, 1, 6), To = new DateOnly(2026, 1, 31) }));
        Assert.Equal([draft], range.Items.Select(i => i.Id));

        var inclusive = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { From = Jan5, To = Jan5 }));
        Assert.Equal(4, inclusive.TotalCount);

        var byNumber = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { Search = "0002" }));
        var hit = Assert.Single(byNumber.Items);
        Assert.EndsWith("-0002", hit.Number);
        Assert.Equal(5, (await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { Search = "po-2026" }))).TotalCount);
        Assert.Empty((await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { Search = "%" }))).Items);
    }

    [Fact]
    public async Task List_rows_carry_totals_and_the_outstanding_value_of_open_orders_only()
    {
        var (draft, open, partial, received, cancelled, _, _) = await SeedListAsync();

        var page = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery()));
        var byId = page.Items.ToDictionary(i => i.Id);

        Assert.Equal((2 * 100L, 0L), (byId[draft].TotalCents, byId[draft].OutstandingCents));
        Assert.Equal((10 * 200L, 10 * 200L), (byId[open].TotalCents, byId[open].OutstandingCents));
        Assert.Equal((10 * 300L, 6 * 300L), (byId[partial].TotalCents, byId[partial].OutstandingCents));
        Assert.Equal((50L, 0L), (byId[received].TotalCents, byId[received].OutstandingCents));
        Assert.Equal((70L, 0L), (byId[cancelled].TotalCents, byId[cancelled].OutstandingCents));
        Assert.All(page.Items, i => Assert.Equal(1, i.LineCount));
        Assert.Equal("Globex", byId[partial].SupplierName);
    }

    [Theory]
    [InlineData("number", false)]
    [InlineData("number", true)]
    [InlineData("supplier", false)]
    [InlineData("supplier", true)]
    [InlineData("status", false)]
    [InlineData("status", true)]
    [InlineData("date", false)]
    [InlineData("date", true)]
    [InlineData("expected", false)]
    [InlineData("expected", true)]
    [InlineData("total", false)]
    [InlineData("total", true)]
    [InlineData("outstanding", false)]
    [InlineData("outstanding", true)]
    [InlineData(null, false)]
    [InlineData("bogus", true)]
    public async Task Every_sort_key_works_in_both_directions(string? sort, bool desc)
    {
        await SeedListAsync();

        var page = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { Sort = sort, Desc = desc }));

        Assert.Equal(5, page.TotalCount);
    }

    [Fact]
    public async Task Sorting_by_total_and_outstanding_orders_by_value()
    {
        var (draft, open, partial, received, cancelled, _, _) = await SeedListAsync();

        var byTotal = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { Sort = "total" }));
        Assert.Equal([received, cancelled, draft, open, partial], byTotal.Items.Select(i => i.Id));

        var byOutstanding = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { Sort = "outstanding", Desc = true }));
        Assert.Equal([open, partial], byOutstanding.Items.Take(2).Select(i => i.Id));
    }

    [Fact]
    public async Task The_default_order_is_newest_order_date_first_and_paging_works()
    {
        var supplier = await NewSupplierAsync();
        var old = await DraftAsync(supplier, new DateOnly(2026, 1, 1));
        var recent = await DraftAsync(supplier, new DateOnly(2026, 5, 1));
        var middle = await DraftAsync(supplier, new DateOnly(2026, 3, 1));

        var all = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery()));
        Assert.Equal([recent.Id, middle.Id, old.Id], all.Items.Select(i => i.Id));

        var second = await QueryAsync(o => o.SearchAsync(new PurchaseOrderListQuery { PageSize = 2, Page = 2 }));
        Assert.Equal([old.Id], second.Items.Select(i => i.Id));
        Assert.Equal(3, second.TotalCount);
    }

    [Fact]
    public async Task Detail_exposes_totals_flags_supplier_details_and_supplier_skus()
    {
        var supplier = await NewSupplierAsync("Acme");
        await using (var context = Db.CreateContext())
        {
            await context.Suppliers.Where(s => s.Id == supplier).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.Email, "orders@acme.example")
                .SetProperty(x => x.Phone, "555-0100")
                .SetProperty(x => x.Address, "1 Road\nTown")
                .SetProperty(x => x.ContactName, "Pat"));
        }

        var product = await ProductAsync("DT-1", costCents: 100);
        await LinkAsync(product, supplier, 100, supplierSku: "ACME-1");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 6, 150));

        var detail = await GetOrderAsync(order.Id);

        Assert.Equal("orders@acme.example", detail.SupplierEmail);
        Assert.Equal("555-0100", detail.SupplierPhone);
        Assert.Equal("1 Road\nTown", detail.SupplierAddress);
        Assert.Equal("Pat", detail.SupplierContactName);
        Assert.True(detail.SupplierIsActive);
        var line = Assert.Single(detail.Lines);
        Assert.Equal("ACME-1", line.SupplierSku);
        Assert.Equal("DT-1", line.Sku);
        Assert.Equal("each", line.Unit);
        Assert.Equal(900L, line.LineTotalCents);
        Assert.Equal(900L, detail.TotalCents);
        Assert.Equal(900L, detail.OutstandingCents);
        Assert.Equal(6, detail.TotalOrdered);
        Assert.Equal(0, detail.TotalReceived);
        Assert.True(detail.CanCancel);
        Assert.True(detail.CanReceive);
        Assert.False(detail.CanEditLines);
        Assert.True(detail.CanEditHeader);
        Assert.False(detail.CanCloseShort);
        Assert.Null(await QueryAsync(o => o.GetAsync(9999)));
    }

    [Fact]
    public async Task Open_lines_for_a_product_list_only_what_is_still_expected_soonest_first()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("OL-1");
        var other = await ProductAsync("OL-2");
        await LinkAsync(product, supplier, 10, leadTimeDays: 20);

        var late = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 5)); // expected Jan 25 (lead time)
        var noDate = await SubmittedAsync(supplier, new PurchaseOrderLineInput(other, 5), new PurchaseOrderLineInput(product, 2));
        var soon = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 3));
        await RunAsync(async o => (await o.UpdateHeaderAsync(soon.Id, soon.RowVersion, new PurchaseOrderHeaderInput(supplier, soon.OrderDate, Jan5.AddDays(2), null))).Succeeded ? Result.Success() : Result.Failure("x", "x"));
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(product, 9));
        var done = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 1));
        await ReceiveAsync(done.Id, [new ReceiveLineInput(done.Lines[0].Id, 1)]);
        var cancelled = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 7));
        await RunAsync(o => o.CancelAsync(cancelled.Id, Actor));
        var partial = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 4));
        await ReceiveAsync(partial.Id, [new ReceiveLineInput(partial.Lines[0].Id, 3)]);

        var lines = await QueryAsync(o => o.GetOpenLinesForProductAsync(product));

        Assert.Equal(4, lines.Count);
        Assert.Equal(soon.Id, lines[0].PurchaseOrderId);
        Assert.Equal(late.Id, lines[1].PurchaseOrderId);
        Assert.DoesNotContain(lines, l => l.PurchaseOrderId == draft.Id || l.PurchaseOrderId == done.Id || l.PurchaseOrderId == cancelled.Id);
        var partialLine = lines.Single(l => l.PurchaseOrderId == partial.Id);
        Assert.Equal(1, partialLine.Outstanding);
        Assert.Equal(PurchaseOrderStatus.PartiallyReceived, partialLine.Status);
        Assert.Equal("Acme", partialLine.SupplierName);
        Assert.Empty(await QueryAsync(o => o.GetOpenLinesForProductAsync(9999)));
    }

    [Fact]
    public async Task On_order_sums_outstanding_over_open_orders_and_never_goes_negative()
    {
        var supplier = await NewSupplierAsync();
        var a = await ProductAsync("OO-A");
        var b = await ProductAsync("OO-B");
        var c = await ProductAsync("OO-C");
        var d = await ProductAsync("OO-D");

        var first = await SubmittedAsync(supplier, new PurchaseOrderLineInput(a, 10), new PurchaseOrderLineInput(b, 4));
        await SubmittedAsync(supplier, new PurchaseOrderLineInput(a, 5));
        await DraftAsync(supplier, null, new PurchaseOrderLineInput(c, 99));
        var over = await SubmittedAsync(supplier, new PurchaseOrderLineInput(d, 2), new PurchaseOrderLineInput(c, 3));
        await ReceiveAsync(first.Id, [new ReceiveLineInput(first.Lines[0].Id, 6)]);
        await ReceiveAsync(over.Id, [new ReceiveLineInput(over.Lines[0].Id, 3)], allowOver: true, actor: AdminActor);

        var onOrder = await QueryAsync(o => o.GetOnOrderByProductAsync());

        Assert.Equal(4 + 5, onOrder[a]);
        Assert.Equal(4, onOrder[b]);
        Assert.Equal(3, onOrder[c]);
        Assert.False(onOrder.ContainsKey(d), "An over-received line has nothing outstanding.");
    }

    [Fact]
    public async Task Recent_orders_for_a_supplier_are_newest_first_and_limited()
    {
        var acme = await NewSupplierAsync("Acme");
        var other = await NewSupplierAsync("Other");
        var ids = new List<int>();
        for (var i = 1; i <= 4; i++)
        {
            ids.Add((await DraftAsync(acme, new DateOnly(2026, i, 1))).Id);
        }

        await DraftAsync(other);

        var recent = await QueryAsync(o => o.GetRecentForSupplierAsync(acme, take: 3));

        Assert.Equal([ids[3], ids[2], ids[1]], recent.Select(r => r.Id));
        Assert.Equal(4, (await QueryAsync(o => o.GetRecentForSupplierAsync(acme))).Count);
    }

    [Fact]
    public async Task Product_lookup_offers_linked_products_first_with_default_costs_and_skips_archived_ones()
    {
        var supplier = await NewSupplierAsync();
        var linked = await ProductAsync("PK-LINK", costCents: 900);
        var unlinked = await ProductAsync("PK-AAA", costCents: 700);
        var archived = await ProductAsync("PK-OLD");
        await LinkAsync(linked, supplier, 450, supplierSku: "S-1", minOrder: 12);
        await ArchiveAsync(archived);
        var draft = await DraftAsync(supplier, null, new PurchaseOrderLineInput(unlinked, 1));

        var all = await QueryAsync(o => o.LookupProductsAsync(supplier, null, draft.Id));

        Assert.Equal([linked, unlinked], all.Select(p => p.ProductId));
        var first = all[0];
        Assert.True(first.IsLinked);
        Assert.Equal("S-1", first.SupplierSku);
        Assert.Equal(450L, first.DefaultUnitCostCents);
        Assert.Equal(12, first.MinOrderQuantity);
        Assert.False(first.AlreadyOnOrder);
        Assert.False(all[1].IsLinked);
        Assert.Equal(700L, all[1].DefaultUnitCostCents);
        Assert.Null(all[1].MinOrderQuantity);
        Assert.True(all[1].AlreadyOnOrder);

        var searched = await QueryAsync(o => o.LookupProductsAsync(supplier, "pk-a"));
        Assert.Equal([unlinked], searched.Select(p => p.ProductId));
        Assert.Empty(await QueryAsync(o => o.LookupProductsAsync(supplier, "PK-OLD")));
        Assert.Empty(await QueryAsync(o => o.LookupProductsAsync(supplier, "%")));
        Assert.Single(await QueryAsync(o => o.LookupProductsAsync(supplier, null, take: 1)));
    }

    // ---- events ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Events_are_published_after_commit_with_who_and_what()
    {
        var supplier = await NewSupplierAsync();
        var product = await ProductAsync("EV-2");
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(product, 2));
        await ReceiveAsync(order.Id, [new ReceiveLineInput(order.Lines[0].Id, 2)]);

        Assert.Equal(
            [PurchaseOrderEventType.Created, PurchaseOrderEventType.Submitted, PurchaseOrderEventType.Received],
            Events.Events.Select(e => e.Type));
        Assert.All(Events.Events, e =>
        {
            Assert.Equal(order.Id, e.PurchaseOrderId);
            Assert.Equal(order.Number, e.Number);
            Assert.Equal(UserId, e.UserId);
            Assert.Equal(Clock.UtcNow, e.OccurredAtUtc);
        });
        Assert.Contains("Received", Events.Events[2].Detail);
    }

    [Fact]
    public async Task Actions_need_a_user()
    {
        var (context, orders) = NewOrders();
        await using (context)
        {
            await Assert.ThrowsAsync<ArgumentException>(() => orders.SubmitAsync(1, new PurchaseOrderActor("")));
            await Assert.ThrowsAsync<ArgumentException>(() => orders.CreateDraftAsync(1, new PurchaseOrderActor(" ")));
            await Assert.ThrowsAsync<ArgumentException>(() => orders.ReceiveAsync(1, [new ReceiveLineInput(1, 1)], new PurchaseOrderActor("")));
        }
    }
}
