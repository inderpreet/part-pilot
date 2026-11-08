using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-03-2: posting, counting, reversing, history and verification.</summary>
public sealed class StockServiceTests : StockTestBase
{
    // ---- each type ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Receive_adds_stock_and_records_who_when_and_the_running_total()
    {
        var id = await NewProductAsync("R-1");

        var result = await PostAsync(new StockMovement(id, StockTransactionType.Receive, 12, Reason: "  Delivery ", Reference: "DN-77", Note: "Pallet 2", UnitCostCents: 250));

        Assert.True(result.Succeeded);
        var posted = Assert.Single(result.Value);
        Assert.Equal(12, posted.QuantityDelta);
        Assert.Equal(12, posted.QuantityAfter);
        Assert.NotNull(posted.TransactionId);
        Assert.Equal(12, await OnHandAsync(id));

        var row = Assert.Single(await LedgerAsync(id));
        Assert.Equal(StockTransactionType.Receive, row.Type);
        Assert.Equal(12, row.QuantityDelta);
        Assert.Equal(12, row.QuantityAfter);
        Assert.Equal("Delivery", row.Reason);
        Assert.Equal("DN-77", row.Reference);
        Assert.Equal("Pallet 2", row.Note);
        Assert.Equal(250L, row.UnitCostCents);
        Assert.Equal(UserId, row.UserId);
        Assert.Equal(Clock.UtcNow, row.OccurredAt);
        Assert.Equal(DateTimeKind.Utc, row.CreatedAt.Kind);
    }

    [Fact]
    public async Task Issue_subtracts_and_return_adds_back()
    {
        var id = await NewProductAsync("I-1", onHand: 10);

        Assert.True((await PostAsync(Issue(id, 4))).Succeeded);
        Assert.Equal(6, await OnHandAsync(id));

        Assert.True((await PostAsync(new StockMovement(id, StockTransactionType.Return, 3, Reason: "Unused on job"))).Succeeded);
        Assert.Equal(9, await OnHandAsync(id));

        var rows = await LedgerAsync(id);
        Assert.Equal([StockTransactionType.Receive, StockTransactionType.Issue, StockTransactionType.Return], rows.Select(r => r.Type));
        Assert.Equal([10, -4, 3], rows.Select(r => r.QuantityDelta));
        Assert.Equal([10, 6, 9], rows.Select(r => r.QuantityAfter));
    }

    [Fact]
    public async Task Adjust_takes_a_signed_quantity_and_needs_a_reason()
    {
        var id = await NewProductAsync("A-1", onHand: 10);

        Assert.True((await PostAsync(Adjust(id, -3, "Damaged"))).Succeeded);
        Assert.True((await PostAsync(Adjust(id, 5, "Found in back room"))).Succeeded);
        Assert.Equal(12, await OnHandAsync(id));

        AssertFailed(await PostAsync(Adjust(id, 2, reason: null)), "stock.reason_required", "Lines[0].Reason");
        AssertFailed(await PostAsync(Adjust(id, 2, reason: "   ")), "stock.reason_required", "Lines[0].Reason");
        AssertFailed(await PostAsync(Adjust(id, 0)), "stock.quantity_zero", "Lines[0].Quantity");
        Assert.Equal(12, await OnHandAsync(id));
    }

    [Fact]
    public async Task Count_sets_the_absolute_quantity_and_stores_the_difference()
    {
        var id = await NewProductAsync("C-1", onHand: 10);

        var up = await PostAsync(Count(id, 14));
        var down = await PostAsync(Count(id, 6));

        Assert.Equal(4, Assert.Single(up.Value).QuantityDelta);
        Assert.Equal(-8, Assert.Single(down.Value).QuantityDelta);
        Assert.Equal(6, await OnHandAsync(id));
        var counts = (await LedgerAsync(id)).Where(r => r.Type == StockTransactionType.Count).ToList();
        Assert.Equal([4, -8], counts.Select(r => r.QuantityDelta));
        Assert.Equal([14, 6], counts.Select(r => r.QuantityAfter));
    }

    [Fact]
    public async Task A_count_that_matches_posts_nothing_and_a_count_to_zero_is_fine()
    {
        var same = await NewProductAsync("C-SAME", onHand: 5);
        var empty = await NewProductAsync("C-ZERO", onHand: 5);

        var result = await PostAsync(Count(same, 5), Count(empty, 0));

        Assert.True(result.Succeeded);
        Assert.Equal(2, result.Value.Count);
        var matched = result.Value.Single(p => p.ProductId == same);
        Assert.Null(matched.TransactionId);
        Assert.Equal(0, matched.QuantityDelta);
        Assert.Equal(5, matched.QuantityAfter);
        Assert.Equal(0, await OnHandAsync(empty));
        Assert.Single(await LedgerAsync(same)); // only the opening receipt
    }

    [Fact]
    public async Task Count_only_posts_the_differing_lines_in_a_batch()
    {
        var ids = new List<int>();
        for (var i = 0; i < 50; i++)
        {
            ids.Add(await NewProductAsync($"BULK-{i:00}", onHand: 10));
        }

        var counts = ids.Select((id, i) => Count(id, i is 3 or 20 or 41 ? 7 : 10)).ToArray();
        var before = (await LedgerAsync()).Count;

        var result = await PostAsync(counts);

        Assert.True(result.Succeeded);
        Assert.Equal(50, result.Value.Count);
        Assert.Equal(3, result.Value.Count(p => p.TransactionId is not null));
        Assert.Equal(before + 3, (await LedgerAsync()).Count);
        Assert.Equal([0, 1, 2, 3], result.Value.Select(p => p.LineIndex).Take(4));
    }

    // ---- validation --------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(StockTransactionType.Receive, 0, "stock.quantity_positive")]
    [InlineData(StockTransactionType.Receive, -2, "stock.quantity_positive")]
    [InlineData(StockTransactionType.Issue, 0, "stock.quantity_positive")]
    [InlineData(StockTransactionType.Issue, -1, "stock.quantity_positive")]
    [InlineData(StockTransactionType.Return, 0, "stock.quantity_positive")]
    [InlineData(StockTransactionType.Count, -1, "stock.count_negative")]
    public async Task Invalid_quantities_are_rejected_and_nothing_is_written(StockTransactionType type, int quantity, string code)
    {
        var id = await NewProductAsync("Q-1", onHand: 5);

        var result = await PostAsync(new StockMovement(id, type, quantity));

        AssertFailed(result, code, "Lines[0].Quantity");
        Assert.Equal(5, await OnHandAsync(id));
        Assert.Single(await LedgerAsync(id));
    }

    [Fact]
    public async Task A_reversal_cannot_be_posted_directly_and_neither_can_an_undefined_type()
    {
        var id = await NewProductAsync("T-1", onHand: 5);

        AssertFailed(await PostAsync(new StockMovement(id, StockTransactionType.Reversal, 1)), "stock.type_invalid", "Lines[0].Type");
        AssertFailed(await PostAsync(new StockMovement(id, (StockTransactionType)99, 1)), "stock.type_invalid", "Lines[0].Type");
    }

    [Fact]
    public async Task An_empty_batch_is_rejected()
    {
        AssertFailed(await PostAsync(), "stock.no_lines");
    }

    [Fact]
    public async Task Unknown_and_archived_products_cannot_receive_movements()
    {
        var archived = await NewProductAsync("ARCH-1", onHand: 3, archived: true);

        AssertFailed(await PostAsync(Receive(archived, 1)), "stock.product_archived", "Lines[0].Product");
        AssertFailed(await PostAsync(Receive(9999, 1)), "stock.product_missing", "Lines[0].Product");
        Assert.Equal(3, await OnHandAsync(archived));
    }

    [Fact]
    public async Task Text_lengths_and_unit_cost_are_checked()
    {
        var id = await NewProductAsync("L-1");

        var result = await PostAsync(new StockMovement(
            id, StockTransactionType.Receive, 1, Reason: new string('r', 101), Reference: new string('f', 101), Note: new string('n', 501), UnitCostCents: -1));

        Assert.False(result.Succeeded);
        Assert.Equal(["Lines[0].Reason", "Lines[0].Reference", "Lines[0].Note", "Lines[0].UnitCost"], result.Errors.Select(e => e.Field));
    }

    [Fact]
    public async Task Dates_may_be_in_the_past_but_not_in_the_future()
    {
        var id = await NewProductAsync("D-1");
        var past = Clock.UtcNow.AddDays(-30);

        Assert.True((await PostAsync(new StockMovement(id, StockTransactionType.Receive, 1, OccurredAt: past))).Succeeded);
        Assert.Equal(past, Assert.Single(await LedgerAsync(id)).OccurredAt);

        AssertFailed(
            await PostAsync(new StockMovement(id, StockTransactionType.Receive, 1, OccurredAt: Clock.UtcNow.AddHours(1))),
            "stock.future_date",
            "Lines[0].OccurredAt");
    }

    [Fact]
    public async Task A_date_a_few_minutes_ahead_is_clamped_to_now()
    {
        var id = await NewProductAsync("D-2");

        Assert.True((await PostAsync(new StockMovement(id, StockTransactionType.Receive, 1, OccurredAt: Clock.UtcNow.AddMinutes(3)))).Succeeded);

        Assert.Equal(Clock.UtcNow, Assert.Single(await LedgerAsync(id)).OccurredAt);
    }

    [Fact]
    public async Task Local_and_unspecified_dates_are_converted_to_utc()
    {
        var id = await NewProductAsync("D-3");
        var utc = Clock.UtcNow.AddDays(-2);
        var local = utc.ToLocalTime();
        var unspecified = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified);

        Assert.True((await PostAsync(
            new StockMovement(id, StockTransactionType.Receive, 1, OccurredAt: local),
            new StockMovement(id, StockTransactionType.Receive, 1, OccurredAt: unspecified))).Succeeded);

        var rows = await LedgerAsync(id);
        Assert.All(rows, r => Assert.Equal(utc, r.OccurredAt));
    }

    // ---- negative stock ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Taking_more_than_is_on_hand_is_rejected_by_default()
    {
        var id = await NewProductAsync("N-1", onHand: 3);

        var result = await PostAsync(Issue(id, 5));

        AssertFailed(result, "stock.insufficient", "Lines[0].Quantity");
        Assert.Contains("only 3 on hand", result.Errors[0].Message);
        Assert.Contains("N-1", result.Errors[0].Message);
        Assert.Equal(3, await OnHandAsync(id));

        AssertFailed(await PostAsync(Adjust(id, -4)), "stock.insufficient");
        Assert.True((await PostAsync(Issue(id, 3))).Succeeded); // exactly down to zero is fine
        Assert.Equal(0, await OnHandAsync(id));
    }

    [Fact]
    public async Task Negative_on_hand_is_allowed_when_the_setting_is_on()
    {
        Settings.AllowNegative = true;
        var id = await NewProductAsync("N-2", onHand: 3);

        Assert.True((await PostAsync(Issue(id, 5))).Succeeded);

        Assert.Equal(-2, await OnHandAsync(id));
        await AssertConsistentAsync(id);
    }

    [Fact]
    public async Task Bringing_negative_stock_back_up_is_allowed_even_if_it_stays_negative_and_the_setting_is_off()
    {
        Settings.AllowNegative = true;
        var id = await NewProductAsync("N-3", onHand: 1);
        Assert.True((await PostAsync(Issue(id, 6))).Succeeded); // -5
        Settings.AllowNegative = false;

        Assert.True((await PostAsync(Receive(id, 2))).Succeeded); // -3: still negative, but it improves things
        AssertFailed(await PostAsync(Issue(id, 1)), "stock.insufficient");
        Assert.Equal(-3, await OnHandAsync(id));
    }

    [Fact]
    public async Task Quantities_beyond_the_maximum_are_rejected()
    {
        var id = await NewProductAsync("MAX-1", onHand: 5);

        AssertFailed(await PostAsync(Receive(id, StockService.MaxQuantity + 1)), "stock.too_large", "Lines[0].Quantity");
        AssertFailed(await PostAsync(Receive(id, StockService.MaxQuantity)), "stock.too_large", "Lines[0].Quantity"); // 5 + max
        Assert.True((await PostAsync(Receive(id, StockService.MaxQuantity - 5))).Succeeded);
        Assert.Equal(StockService.MaxQuantity, await OnHandAsync(id));
    }

    // ---- batches -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task One_bad_line_rolls_back_the_whole_batch()
    {
        var a = await NewProductAsync("B-A", onHand: 5);
        var b = await NewProductAsync("B-B", onHand: 5);
        var before = (await LedgerAsync()).Count;

        var result = await PostAsync(Receive(a, 10), Receive(b, 10), Issue(b, 99));

        AssertFailed(result, "stock.insufficient", "Lines[2].Quantity");
        Assert.Equal(5, await OnHandAsync(a));
        Assert.Equal(5, await OnHandAsync(b));
        Assert.Equal(before, (await LedgerAsync()).Count);
    }

    [Fact]
    public async Task Every_bad_line_is_reported_with_its_own_index()
    {
        var a = await NewProductAsync("E-A", onHand: 1);

        var result = await PostAsync(Receive(a, 0), Receive(a, 1), Issue(a, 50), Adjust(a, 1, reason: null));

        Assert.False(result.Succeeded);
        Assert.Equal(
            ["Lines[0].Quantity", "Lines[3].Reason"],
            result.Errors.Select(e => e.Field).Order(StringComparer.Ordinal)); // shape errors come first and stop before the db checks
    }

    [Fact]
    public async Task Database_level_errors_are_also_reported_per_line_before_anything_is_written()
    {
        var a = await NewProductAsync("E-B", onHand: 1);

        var result = await PostAsync(Issue(a, 50), Receive(9999, 1));

        Assert.Equal(["Lines[0].Quantity", "Lines[1].Product"], result.Errors.Select(e => e.Field));
        Assert.Equal(1, await OnHandAsync(a));
    }

    [Fact]
    public async Task Lines_for_the_same_product_use_the_running_balance()
    {
        var id = await NewProductAsync("S-1", onHand: 2);

        var ok = await PostAsync(Receive(id, 5), Issue(id, 6), Adjust(id, 1));
        Assert.True(ok.Succeeded);
        Assert.Equal([7, 1, 2], ok.Value.Select(p => p.QuantityAfter));
        Assert.Equal(2, await OnHandAsync(id));

        // 2 + 1 = 3, then issuing 4 would go negative.
        AssertFailed(await PostAsync(Receive(id, 1), Issue(id, 4)), "stock.insufficient", "Lines[1].Quantity");
        Assert.Equal(2, await OnHandAsync(id));
        await AssertConsistentAsync(id);
    }

    [Fact]
    public async Task A_count_inside_a_batch_is_measured_against_the_balance_at_that_point()
    {
        var id = await NewProductAsync("S-2", onHand: 10);

        var result = await PostAsync(Issue(id, 4), Count(id, 6)); // after the issue it is already 6: the count matches

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Value[0].TransactionId);
        Assert.Null(result.Value[1].TransactionId);
        Assert.Equal(6, await OnHandAsync(id));
    }

    [Fact]
    public async Task Rows_in_a_batch_get_ids_in_line_order()
    {
        var a = await NewProductAsync("O-A");
        var b = await NewProductAsync("O-B");

        var result = await PostAsync(Receive(a, 1), Receive(b, 1), Receive(a, 1), Receive(b, 1));

        var ids = result.Value.Select(p => p.TransactionId!.Value).ToList();
        Assert.Equal(ids.Order(), ids);
        await AssertConsistentAsync(a);
        await AssertConsistentAsync(b);
    }

    // ---- reversal ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Reversing_posts_the_opposite_transaction_and_links_it()
    {
        var id = await NewProductAsync("V-1", onHand: 10);
        var issue = (await PostAsync(Issue(id, 4))).Value[0].TransactionId!.Value;

        var result = await ReverseAsync(issue, note: "Wrong product");

        Assert.True(result.Succeeded);
        Assert.Equal(4, result.Value.QuantityDelta);
        Assert.Equal(10, result.Value.QuantityAfter);
        Assert.Equal(10, await OnHandAsync(id));

        var reversal = (await LedgerAsync(id)).Last();
        Assert.Equal(StockTransactionType.Reversal, reversal.Type);
        Assert.Equal(issue, reversal.ReversesTransactionId);
        Assert.Equal($"Reversal of #{issue}", reversal.Reason);
        Assert.Equal("Wrong product", reversal.Note);
        Assert.Equal(UserId, reversal.UserId);
        await AssertConsistentAsync(id);
    }

    [Fact]
    public async Task A_transaction_can_only_be_reversed_once_and_a_reversal_cannot_be_reversed()
    {
        var id = await NewProductAsync("V-2", onHand: 10);
        var receive = (await LedgerAsync(id)).Single().Id;

        var first = await ReverseAsync(receive);
        Assert.True(first.Succeeded);

        AssertFailed(await ReverseAsync(receive), "stock.already_reversed");
        AssertFailed(await ReverseAsync(first.Value.TransactionId!.Value), "stock.reverse_reversal");
        AssertFailed(await ReverseAsync(123456), "stock.transaction_missing");
        Assert.Equal(0, await OnHandAsync(id));
        Assert.Equal(2, (await LedgerAsync(id)).Count);
    }

    [Fact]
    public async Task Reversing_a_count_restores_the_previous_quantity()
    {
        var id = await NewProductAsync("V-3", onHand: 5);
        var count = (await PostAsync(Count(id, 8))).Value[0].TransactionId!.Value;

        Assert.True((await ReverseAsync(count)).Succeeded);

        Assert.Equal(5, await OnHandAsync(id));
    }

    [Fact]
    public async Task Reversing_a_receipt_that_has_since_been_used_is_blocked_unless_negative_stock_is_allowed()
    {
        var id = await NewProductAsync("V-4");
        var receipt = (await PostAsync(Receive(id, 10))).Value[0].TransactionId!.Value;
        Assert.True((await PostAsync(Issue(id, 8))).Succeeded);

        var blocked = await ReverseAsync(receipt);
        AssertFailed(blocked, "stock.insufficient");
        Assert.Contains("V-4", blocked.Errors[0].Message);
        Assert.Equal(2, await OnHandAsync(id));

        Settings.AllowNegative = true;
        Assert.True((await ReverseAsync(receipt)).Succeeded);
        Assert.Equal(-8, await OnHandAsync(id));
    }

    [Fact]
    public async Task An_archived_product_can_still_have_a_transaction_reversed()
    {
        var id = await NewProductAsync("V-5", onHand: 5);
        var receipt = (await LedgerAsync(id)).Single().Id;
        await ArchiveAsync(id);

        Assert.True((await ReverseAsync(receipt)).Succeeded);

        Assert.Equal(0, await OnHandAsync(id));
    }

    [Fact]
    public async Task A_reversal_carries_over_the_cost_reference_and_purchase_order_line()
    {
        var id = await NewProductAsync("V-6");
        var lineId = await NewPurchaseOrderLineAsync(id);
        var original = (await PostAsync(new StockMovement(id, StockTransactionType.Receive, 5, Reference: "PO-1", UnitCostCents: 199, PurchaseOrderLineId: lineId))).Value[0].TransactionId!.Value;

        await ReverseAsync(original);

        var reversal = (await LedgerAsync(id)).Last();
        Assert.Equal(199L, reversal.UnitCostCents);
        Assert.Equal("PO-1", reversal.Reference);
        Assert.Equal(lineId, reversal.PurchaseOrderLineId);
    }

    [Fact]
    public async Task A_too_long_reversal_note_is_rejected()
    {
        var id = await NewProductAsync("V-7", onHand: 1);
        var receipt = (await LedgerAsync(id)).Single().Id;

        AssertFailed(await ReverseAsync(receipt, new string('n', 501)), "stock.note_too_long", "Note");
    }

    // ---- the cache and the product -----------------------------------------------------------------------------

    [Fact]
    public async Task Moving_stock_does_not_change_the_product_row_version_so_open_edit_forms_still_save()
    {
        var created = await CreateProductAsync(Input(sku: "RV-1"));
        Assert.True((await PostAsync(Receive(created.Id, 5))).Succeeded);

        var (context, products, _) = NewRequest();
        await using var _ = context;
        var result = await products.UpdateAsync(created.Id, created.RowVersion, Input(sku: "RV-1", name: "Renamed"));

        Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Message)));
        Assert.Equal(5, result.Value.QuantityOnHand); // the edit did not overwrite the cached quantity either
        Assert.Equal("Renamed", result.Value.Name);
    }

    [Fact]
    public async Task The_sku_is_locked_once_the_product_has_stock_history()
    {
        var created = await CreateProductAsync(Input(sku: "LOCK-1"));
        Assert.False(created.SkuLocked);
        Assert.True((await PostAsync(Receive(created.Id, 1))).Succeeded);

        var (context, products, _) = NewRequest();
        await using var _ = context;

        Assert.True((await products.GetAsync(created.Id))!.SkuLocked);
        var renamed = await products.UpdateAsync(created.Id, created.RowVersion, Input(sku: "LOCK-2"));
        AssertFailed(renamed, "product.sku_locked", "Sku");

        var sameSku = await products.UpdateAsync(created.Id, created.RowVersion, Input(sku: "LOCK-1", name: "Other name"));
        Assert.True(sameSku.Succeeded);
        Assert.True(sameSku.Value.SkuLocked);
    }

    [Fact]
    public async Task Stock_posted_through_a_callers_transaction_is_rolled_back_with_it()
    {
        var id = await NewProductAsync("TX-1", onHand: 5);

        var (rolledBack, stock) = NewStock();
        await using (rolledBack)
        {
            await using var transaction = await rolledBack.Database.BeginTransactionAsync();
            Assert.True((await stock.PostAsync([Receive(id, 10)], Who)).Succeeded);
            await transaction.RollbackAsync();
        }

        Assert.Equal(5, await OnHandAsync(id));
        Assert.Single(await LedgerAsync(id));

        var (committed, stock2) = NewStock();
        await using (committed)
        {
            await using var transaction = await committed.Database.BeginTransactionAsync();
            Assert.True((await stock2.PostAsync([Receive(id, 10)], Who)).Succeeded);
            await transaction.CommitAsync();
        }

        Assert.Equal(15, await OnHandAsync(id));
    }

    [Fact]
    public async Task Posting_leaves_the_callers_context_clean_so_a_later_save_does_not_duplicate_rows()
    {
        var id = await NewProductAsync("CLEAN-1");
        var (context, stock) = NewStock();
        await using var _ = context;

        Assert.True((await stock.PostAsync([Receive(id, 3)], Who)).Succeeded);
        await context.SaveChangesAsync();

        Assert.Empty(context.ChangeTracker.Entries<StockTransaction>());
        Assert.Single(await LedgerAsync(id));
    }

    [Fact]
    public async Task A_missing_user_id_is_a_programming_error()
    {
        var (context, stock) = NewStock();
        await using var _ = context;

        await Assert.ThrowsAsync<ArgumentException>(() => stock.PostAsync([Receive(1, 1)], new StockContext(" ")));
        await Assert.ThrowsAsync<ArgumentException>(() => stock.ReverseAsync(1, new StockContext("")));
    }

    // ---- queries -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task History_lists_one_products_rows_newest_posting_first_with_reversal_flags()
    {
        var id = await NewProductAsync("H-1");
        var other = await NewProductAsync("H-2", onHand: 99);
        var first = (await PostAsync(Receive(id, 10, reference: "PO-9"))).Value[0].TransactionId!.Value;
        var second = (await PostAsync(Issue(id, 3))).Value[0].TransactionId!.Value;
        var reversal = (await ReverseAsync(second)).Value.TransactionId!.Value;

        var (context, stock) = NewStock();
        await using var _ = context;
        var page = await stock.GetHistoryAsync(id, new ListQuery());

        Assert.Equal(3, page.TotalCount);
        Assert.Equal([reversal, second, first], page.Items.Select(i => i.Id));
        Assert.All(page.Items, i => Assert.Equal(id, i.ProductId));
        Assert.Equal("Mo Ver", page.Items[0].UserName);
        Assert.Equal("H-1", page.Items[0].Sku);

        Assert.Equal(StockTransactionType.Reversal, page.Items[0].Type);
        Assert.Equal(second, page.Items[0].ReversesTransactionId);
        Assert.False(page.Items[0].CanReverse);
        Assert.True(page.Items[1].IsReversed);
        Assert.Equal(reversal, page.Items[1].ReversedByTransactionId);
        Assert.False(page.Items[1].CanReverse);
        Assert.False(page.Items[2].IsReversed);
        Assert.True(page.Items[2].CanReverse);
        Assert.Equal("PO-9", page.Items[2].Reference);
        Assert.NotEqual(other, page.Items[0].ProductId);
    }

    [Fact]
    public async Task History_is_paged()
    {
        var id = await NewProductAsync("H-PAGE");
        for (var i = 0; i < 7; i++)
        {
            Assert.True((await PostAsync(Receive(id, 1))).Succeeded);
        }

        var (context, stock) = NewStock();
        await using var _ = context;
        var page2 = await stock.GetHistoryAsync(id, new ListQuery { Page = 2, PageSize = 3 });

        Assert.Equal(7, page2.TotalCount);
        Assert.Equal(3, page2.Items.Count);
        Assert.Equal(3, page2.TotalPages);
        Assert.Equal([4, 3, 2], page2.Items.Select(i => i.QuantityAfter));
    }

    [Fact]
    public async Task Get_returns_one_row_or_null()
    {
        var id = await NewProductAsync("G-1");
        var txId = (await PostAsync(Receive(id, 4))).Value[0].TransactionId!.Value;

        var (context, stock) = NewStock();
        await using var _ = context;

        var item = await stock.GetAsync(txId);
        Assert.NotNull(item);
        Assert.Equal(4, item.QuantityDelta);
        Assert.Null(await stock.GetAsync(98765));
    }

    [Fact]
    public async Task Movements_can_be_filtered_by_type_product_user_date_and_search_text()
    {
        var hammer = await NewProductAsync("HAM-1");
        var nails = await NewProductAsync("NAIL-1");

        Clock.UtcNow = new DateTime(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
        await PostAsync(new StockMovement(hammer, StockTransactionType.Receive, 10, Reference: "PO-100", OccurredAt: Clock.UtcNow));
        Clock.UtcNow = new DateTime(2026, 6, 10, 9, 0, 0, DateTimeKind.Utc);
        await PostAsync(new StockMovement(nails, StockTransactionType.Receive, 500, Reason: "Bulk delivery", OccurredAt: Clock.UtcNow));
        Clock.UtcNow = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc);
        await PostAsync(Issue(hammer, 2));

        string otherUser;
        await using (var seed = Db.CreateContext())
        {
            var user = new ApplicationUser { UserName = "other", DisplayName = "Other Person" };
            seed.Users.Add(user);
            await seed.SaveChangesAsync();
            otherUser = user.Id;
        }

        var (context, stock) = NewStock();
        await using var _ = context;
        await stock.PostAsync([Issue(nails, 1)], new StockContext(otherUser));

        async Task<List<string>> Skus(MovementQuery q) => (await stock.SearchMovementsAsync(q)).Items.Select(i => i.Sku + ":" + i.QuantityDelta).ToList();

        Assert.Equal(4, (await stock.SearchMovementsAsync(new MovementQuery())).TotalCount);
        Assert.Equal(["NAIL-1:-1", "HAM-1:-2", "NAIL-1:500", "HAM-1:10"], await Skus(new MovementQuery())); // newest posting first
        Assert.Equal(["HAM-1:-2"], await Skus(new MovementQuery { Type = StockTransactionType.Issue, ProductId = hammer }));
        Assert.Equal(["NAIL-1:-1"], await Skus(new MovementQuery { UserId = otherUser }));
        Assert.Equal(["NAIL-1:500"], await Skus(new MovementQuery { From = new DateTime(2026, 6, 5, 0, 0, 0, DateTimeKind.Utc), To = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc) }));
        Assert.Equal(["HAM-1:10"], await Skus(new MovementQuery { Search = "PO-100" }));
        Assert.Equal(["NAIL-1:500"], await Skus(new MovementQuery { Search = "bulk" }));
        Assert.Equal(["HAM-1:-2", "HAM-1:10"], await Skus(new MovementQuery { Search = "ham" }));
        Assert.Empty(await Skus(new MovementQuery { Search = "%" }));

        // From is inclusive, To exclusive.
        Assert.Equal(["HAM-1:-2"], await Skus(new MovementQuery { ProductId = hammer, From = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc) }));
        Assert.Equal(["HAM-1:10"], await Skus(new MovementQuery { ProductId = hammer, To = new DateTime(2026, 6, 15, 9, 0, 0, DateTimeKind.Utc) }));
    }

    [Fact]
    public async Task Count_preview_shows_differences_and_skips_unknown_products()
    {
        var a = await NewProductAsync("PV-A", onHand: 10);
        var b = await NewProductAsync("PV-B", onHand: 4);

        var (context, stock) = NewStock();
        await using var _ = context;
        var preview = await stock.PreviewCountAsync([new CountEntry(a, 7), new CountEntry(9999, 1), new CountEntry(b, 4)]);

        Assert.Equal(2, preview.Count);
        Assert.Equal(("PV-A", 10, 7, -3), (preview[0].Sku, preview[0].OnHand, preview[0].Counted, preview[0].Difference));
        Assert.Equal(0, preview[1].Difference);
        Assert.Equal(10, await OnHandAsync(a)); // nothing was written
    }

    // ---- verify ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Verify_reports_a_consistent_database_as_clean()
    {
        await NewProductAsync("VF-1", onHand: 5);
        await NewProductAsync("VF-2");

        var (context, stock) = NewStock();
        await using var _ = context;
        var report = await stock.VerifyAsync();

        Assert.True(report.IsConsistent);
        Assert.Equal(2, report.ProductsChecked);
        Assert.False(report.Fixed);
    }

    [Fact]
    public async Task Verify_detects_a_tampered_cache_and_fix_repairs_it_from_the_ledger()
    {
        var id = await NewProductAsync("VF-3", onHand: 8);
        var untouched = await NewProductAsync("VF-4", onHand: 2);
        await SetOnHandAsync(id, 99);

        var (context, stock) = NewStock();
        await using var _ = context;

        var report = await stock.VerifyAsync();
        var mismatch = Assert.Single(report.Mismatches);
        Assert.Equal((id, "VF-3", 99, 8), (mismatch.ProductId, mismatch.Sku, mismatch.Cached, mismatch.Ledger));
        Assert.False(report.IsConsistent);
        Assert.Equal(99, await OnHandAsync(id)); // reporting alone repairs nothing

        var fixedReport = await stock.VerifyAsync(fix: true);
        Assert.Single(fixedReport.Mismatches);
        Assert.True(fixedReport.Fixed);
        Assert.Equal(8, await OnHandAsync(id));
        Assert.Equal(2, await OnHandAsync(untouched));
        Assert.True((await stock.VerifyAsync()).IsConsistent);
        Assert.False((await stock.VerifyAsync(fix: true)).Fixed); // nothing left to fix
    }

    [Fact]
    public async Task Verify_detects_a_tampered_ledger_when_the_triggers_have_been_bypassed()
    {
        var id = await NewProductAsync("VF-5", onHand: 8);
        await using (var context = Db.CreateContext())
        {
            await context.Database.ExecuteSqlRawAsync("DROP TRIGGER TR_StockTransactions_NoUpdate");
            await context.Database.ExecuteSqlRawAsync("UPDATE StockTransactions SET QuantityDelta = 80");
        }

        var (verifyContext, stock) = NewStock();
        await using var _ = verifyContext;
        var mismatch = Assert.Single((await stock.VerifyAsync()).Mismatches);

        Assert.Equal((id, 8, 80), (mismatch.ProductId, mismatch.Cached, mismatch.Ledger));
    }

    [Fact]
    public async Task A_product_with_a_cache_but_no_ledger_is_a_mismatch()
    {
        var id = await NewProductAsync("VF-6");
        await SetOnHandAsync(id, 3);

        var (context, stock) = NewStock();
        await using var _ = context;

        var mismatch = Assert.Single((await stock.VerifyAsync()).Mismatches);
        Assert.Equal((3, 0), (mismatch.Cached, mismatch.Ledger));
    }

    // ---- invariant ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task After_a_thousand_random_operations_every_cache_equals_its_ledger_sum()
    {
        var random = new Random(20260615);
        var products = new List<int>();
        for (var i = 0; i < 6; i++)
        {
            products.Add(await NewProductAsync($"RND-{i}"));
        }

        var types = new[] { StockTransactionType.Receive, StockTransactionType.Issue, StockTransactionType.Adjust, StockTransactionType.Count, StockTransactionType.Return };
        var succeeded = 0;
        var rejected = 0;

        StockMovement RandomLine()
        {
            var type = types[random.Next(types.Length)];
            var quantity = type switch
            {
                StockTransactionType.Adjust => random.Next(-8, 9),
                StockTransactionType.Count => random.Next(0, 40),
                _ => random.Next(0, 9), // 0 is invalid on purpose
            };
            return new StockMovement(products[random.Next(products.Count)], type, quantity, Reason: "random");
        }

        for (var op = 0; op < 1000; op++)
        {
            Settings.AllowNegative = random.Next(10) == 0;
            var (context, stock) = NewStock();
            await using (context)
            {
                Result result;
                var roll = random.Next(100);
                if (roll < 15)
                {
                    var lastId = await context.StockTransactions.Select(t => (long?)t.Id).MaxAsync() ?? 1;
                    result = await stock.ReverseAsync(random.NextInt64(1, lastId + 1), Who);
                }
                else if (roll < 35)
                {
                    result = await stock.PostAsync([.. Enumerable.Range(0, random.Next(2, 5)).Select(_ => RandomLine())], Who);
                }
                else
                {
                    result = await stock.PostAsync([RandomLine()], Who);
                }

                if (result.Succeeded)
                {
                    succeeded++;
                }
                else
                {
                    rejected++;
                }
            }
        }

        Assert.True(succeeded > 300, $"Only {succeeded} operations succeeded; the test is not exercising enough.");
        Assert.True(rejected > 50, $"Only {rejected} operations were rejected; the rules are not being exercised.");
        foreach (var id in products)
        {
            await AssertConsistentAsync(id);
        }

        var (verifyContext, verifier) = NewStock();
        await using var _ = verifyContext;
        Assert.True((await verifier.VerifyAsync()).IsConsistent);

        // Every reversal points at exactly one distinct, non-reversal original.
        var ledger = await LedgerAsync();
        var reversals = ledger.Where(t => t.Type == StockTransactionType.Reversal).ToList();
        Assert.Equal(reversals.Count, reversals.Select(r => r.ReversesTransactionId).Distinct().Count());
        Assert.All(reversals, r => Assert.NotEqual(StockTransactionType.Reversal, ledger.Single(t => t.Id == r.ReversesTransactionId).Type));
    }
}
