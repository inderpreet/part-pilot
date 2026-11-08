using InventoryTracker.Data.Entities;
using InventoryTracker.Services;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Tests.Services;

/// <summary>WI-06-2: report filters, totals and CSV exports (the CSV must carry the same rows and totals as the screen).</summary>
public sealed class ReportServiceTests : ReportingTestBase
{
    private DateTime Ago(int days) => Clock.UtcNow.AddDays(-days);

    private static StockMovement At(int productId, StockTransactionType type, int quantity, DateTime when, string? reference = null, string? reason = null)
        => new(productId, type, quantity, Reason: reason, Reference: reference, OccurredAt: when);

    private async Task<int> PartAsync(string sku, long costCents = 100, int? categoryId = null)
        => (await CreateProductAsync(Input(sku: sku, name: "Part " + sku, costCents: costCents, categoryId: categoryId))).Id;

    // ---- valuation ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Valuation_values_stock_at_the_current_unit_cost_and_totals_match_the_rows()
    {
        var fixings = (await CreateCategoryAsync("Fixings")).Id;
        var a = await PartAsync("A", 250, fixings);
        var b = await PartAsync("B", 1000);
        Assert.True((await PostAsync(Receive(a, 10), Receive(b, 3))).Succeeded);

        var report = await WithReportsAsync(s => s.GetValuationAsync(new ValuationFilter { Sort = "sku" }));

        Assert.Equal([2500L, 3000L], report.Page.Items.Select(r => r.ValueCents));
        Assert.Equal(new ValuationTotals(2, 13, 5500), report.Totals);
        Assert.Equal(5500, report.ByCategory.Sum(c => c.ValueCents));
        Assert.Equal([null, "Fixings"], report.ByCategory.Select(c => c.CategoryName));   // biggest value first; null = no category
    }

    [Fact]
    public async Task Valuation_hides_archived_parts_unless_asked_and_filters_by_category_and_search()
    {
        var fixings = (await CreateCategoryAsync("Fixings")).Id;
        var a = await PartAsync("A", 100, fixings);
        var old = await PartAsync("OLD", 100);
        await PartAsync("B");
        Assert.True((await PostAsync(Receive(a, 1), Receive(old, 1))).Succeeded);
        await ArchiveAsync(old);

        var live = await WithReportsAsync(s => s.GetValuationAsync(new ValuationFilter()));
        var all = await WithReportsAsync(s => s.GetValuationAsync(new ValuationFilter { IncludeArchived = true }));
        var category = await WithReportsAsync(s => s.GetValuationAsync(new ValuationFilter { CategoryId = fixings }));
        var search = await WithReportsAsync(s => s.GetValuationAsync(new ValuationFilter { Search = "part b" }));

        Assert.Equal(2, live.Totals.Products);
        Assert.Equal(3, all.Totals.Products);
        Assert.Equal(["A"], category.Page.Items.Select(r => r.Sku));
        Assert.Equal(["B"], search.Page.Items.Select(r => r.Sku));
    }

    [Fact]
    public async Task Valuation_csv_has_a_header_and_one_row_per_part_and_the_same_value_total()
    {
        var a = await PartAsync("A", 250);
        var b = await PartAsync("B", 1000);
        Assert.True((await PostAsync(Receive(a, 10), Receive(b, 3))).Succeeded);
        var filter = new ValuationFilter { PageSize = 1 };

        var csv = CsvLines(await CsvAsync((s, stream) => s.WriteValuationCsvAsync(filter, stream)));
        var report = await WithReportsAsync(s => s.GetValuationAsync(filter));

        Assert.Equal("SKU,Name,Category,Unit,On hand,Unit cost,Value,Active", csv[0]);
        Assert.Equal(3, csv.Length);   // header + both parts, although the screen showed a page of one
        Assert.Equal(report.Totals.ValueCents, csv.Skip(1).Sum(l => (long)(decimal.Parse(l.Split(',')[6], System.Globalization.CultureInfo.InvariantCulture) * 100)));
        Assert.Equal(2, report.Page.TotalCount);
    }

    // ---- movements ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Movements_filter_by_period_type_part_category_user_and_search()
    {
        var fixings = (await CreateCategoryAsync("Fixings")).Id;
        var a = await PartAsync("A", categoryId: fixings);
        var b = await PartAsync("B");
        Assert.True((await PostAsync(
            At(a, StockTransactionType.Receive, 10, Ago(10), reference: "PO-ONE"),
            At(a, StockTransactionType.Issue, 4, Ago(5)),
            At(b, StockTransactionType.Receive, 7, Ago(2)),
            At(b, StockTransactionType.Adjust, -1, Ago(1), reason: "Damaged in transit"))).Succeeded);

        MovementReportFilter Filter(Action<MovementReportFilter>? tweak = null)
        {
            var f = new MovementReportFilter();
            tweak?.Invoke(f);
            return f;
        }

        var all = await WithReportsAsync(s => s.GetMovementsAsync(Filter()));
        var period = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => { f.FromUtc = Ago(6); f.ToUtcExclusive = Ago(1); })));
        var issues = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => f.Type = StockTransactionType.Issue)));
        var forB = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => f.ProductId = b)));
        var category = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => f.CategoryId = fixings)));
        var user = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => f.UserId = UserId)));
        var nobody = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => f.UserId = "someone-else")));
        var byReason = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => f.Search = "transit")));
        var byReference = await WithReportsAsync(s => s.GetMovementsAsync(Filter(f => f.Search = "PO-ONE")));

        Assert.Equal(4, all.Totals.Count);
        Assert.Equal([-1, 7, -4, 10], all.Page.Items.Select(r => r.QuantityDelta));    // newest first
        Assert.Equal([7, -4], period.Page.Items.Select(r => r.QuantityDelta));          // from inclusive, to exclusive
        Assert.Single(issues.Page.Items);
        Assert.Equal(2, forB.Totals.Count);
        Assert.Equal(2, category.Totals.Count);
        Assert.Equal(4, user.Totals.Count);
        Assert.Equal(0, nobody.Totals.Count);
        Assert.Equal(-1, Assert.Single(byReason.Page.Items).QuantityDelta);
        Assert.Equal(10, Assert.Single(byReference.Page.Items).QuantityDelta);
    }

    [Fact]
    public async Task Movement_totals_split_into_in_and_out_across_all_pages_and_the_csv_has_every_row()
    {
        var a = await PartAsync("A");
        Assert.True((await PostAsync(
            At(a, StockTransactionType.Receive, 10, Ago(3)),
            At(a, StockTransactionType.Receive, 5, Ago(2)),
            At(a, StockTransactionType.Issue, 4, Ago(1)),
            At(a, StockTransactionType.Adjust, -2, Ago(1), reason: "Lost"))).Succeeded);
        var filter = new MovementReportFilter { PageSize = 2 };

        var report = await WithReportsAsync(s => s.GetMovementsAsync(filter));
        var csv = CsvLines(await CsvAsync((s, stream) => s.WriteMovementsCsvAsync(filter, stream)));

        Assert.Equal(2, report.Page.Items.Count);
        Assert.Equal(new MovementTotals(4, 15, 6), report.Totals);
        Assert.StartsWith("Date (UTC),SKU,Name,Category,Type,Quantity change,On hand after,Unit cost,Reason,Reference,User", csv[0]);
        Assert.Equal(report.Totals.Count + 1, csv.Length);
    }

    // ---- usage ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Usage_sums_issues_per_part_for_the_period_biggest_first_and_ignores_other_movements()
    {
        var a = await PartAsync("A", 100);
        var b = await PartAsync("B", 300);
        var c = await PartAsync("C");
        Assert.True((await PostAsync(
            At(a, StockTransactionType.Receive, 100, Ago(40)),
            At(b, StockTransactionType.Receive, 100, Ago(40)),
            At(c, StockTransactionType.Receive, 100, Ago(40)),
            At(a, StockTransactionType.Issue, 5, Ago(20)),
            At(a, StockTransactionType.Issue, 15, Ago(3)),
            At(b, StockTransactionType.Issue, 10, Ago(3)),
            At(c, StockTransactionType.Adjust, -50, Ago(3), reason: "Lost"))).Succeeded);

        var all = await WithReportsAsync(s => s.GetUsageAsync(new UsageFilter()));
        var recent = await WithReportsAsync(s => s.GetUsageAsync(new UsageFilter { FromUtc = Ago(10) }));

        Assert.Equal(["A", "B"], all.Page.Items.Select(r => r.Sku));
        Assert.Equal(20, all.Page.Items[0].UnitsUsed);
        Assert.Equal(2, all.Page.Items[0].Issues);
        Assert.Equal(20 * 100L, all.Page.Items[0].ValueCents);
        Assert.Equal(new UsageTotals(2, 30, 20 * 100 + 10 * 300), all.Totals);
        Assert.Equal([15L, 10L], recent.Page.Items.Select(r => r.UnitsUsed));
    }

    [Fact]
    public async Task Usage_sorts_pages_and_exports_the_same_rows()
    {
        var parts = new List<int>();
        for (var i = 1; i <= 5; i++)
        {
            var id = await PartAsync($"P{i}");
            parts.Add(id);
            Assert.True((await PostAsync(Receive(id, 50), Issue(id, i))).Succeeded);
        }

        var bySku = await WithReportsAsync(s => s.GetUsageAsync(new UsageFilter { Sort = "sku", PageSize = 2, Page = 2 }));
        var ascending = await WithReportsAsync(s => s.GetUsageAsync(new UsageFilter { Sort = "units" }));
        var csv = CsvLines(await CsvAsync((s, stream) => s.WriteUsageCsvAsync(new UsageFilter { PageSize = 2 }, stream)));

        Assert.Equal(["P3", "P4"], bySku.Page.Items.Select(r => r.Sku));
        Assert.Equal(5, bySku.Page.TotalCount);
        Assert.Equal([1L, 2L, 3L, 4L, 5L], ascending.Page.Items.Select(r => r.UnitsUsed));
        Assert.Equal("SKU,Name,Category,Unit,Units used,Issues,Value at current cost", csv[0]);
        Assert.Equal(6, csv.Length);
    }

    // ---- purchase orders ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Purchase_order_report_filters_by_dates_supplier_and_status_and_totals_per_supplier()
    {
        var acme = await NewSupplierAsync("Acme");
        var bolt = await NewSupplierAsync("Bolt Co");
        var a = await LowPartAsync("A", acme, costCents: 100);
        var b = await LowPartAsync("B", bolt, costCents: 200);

        var early = await DraftAsync(acme, new DateOnly(2026, 1, 10), new PurchaseOrderLineInput(a, 10));             // 1,000, draft
        var mid = await DraftAsync(acme, new DateOnly(2026, 2, 10), new PurchaseOrderLineInput(a, 5));                // 500
        var other = await DraftAsync(bolt, new DateOnly(2026, 2, 20), new PurchaseOrderLineInput(b, 3));              // 600
        Assert.True((await QueryAsync(o => o.SubmitAsync(mid.Id, Actor))).Succeeded);

        var all = await WithReportsAsync(s => s.GetPurchaseOrdersAsync(new PurchaseOrderReportFilter()));
        var february = await WithReportsAsync(s => s.GetPurchaseOrdersAsync(new PurchaseOrderReportFilter { From = new DateOnly(2026, 2, 1), To = new DateOnly(2026, 2, 28) }));
        var onlyOnTheDay = await WithReportsAsync(s => s.GetPurchaseOrdersAsync(new PurchaseOrderReportFilter { From = new DateOnly(2026, 2, 10), To = new DateOnly(2026, 2, 10) }));
        var acmeOnly = await WithReportsAsync(s => s.GetPurchaseOrdersAsync(new PurchaseOrderReportFilter { SupplierId = acme }));
        var submitted = await WithReportsAsync(s => s.GetPurchaseOrdersAsync(new PurchaseOrderReportFilter { Status = PurchaseOrderStatus.Submitted }));

        Assert.Equal([other.Id, mid.Id, early.Id], all.Page.Items.Select(r => r.Id));   // newest order date first
        Assert.Equal(new PurchaseOrderTotals(3, 1_000 + 500 + 600, 500), all.Totals);   // only the submitted order is outstanding
        Assert.Equal([other.Id, mid.Id], february.Page.Items.Select(r => r.Id));
        Assert.Equal([mid.Id], onlyOnTheDay.Page.Items.Select(r => r.Id));
        Assert.Equal(2, acmeOnly.Totals.Orders);
        Assert.Equal([mid.Id], submitted.Page.Items.Select(r => r.Id));

        var bySupplier = all.BySupplier.ToDictionary(s => s.SupplierName);
        Assert.Equal(1_500, bySupplier["Acme"].TotalCents);
        Assert.Equal(500, bySupplier["Acme"].OutstandingCents);
        Assert.Equal(600, bySupplier["Bolt Co"].TotalCents);
        Assert.Equal(0, bySupplier["Bolt Co"].OutstandingCents);
    }

    [Fact]
    public async Task Purchase_order_outstanding_drops_as_goods_arrive_and_the_csv_matches_the_screen()
    {
        var supplier = await NewSupplierAsync("Acme");
        var part = await LowPartAsync("A", supplier, costCents: 100);
        var order = await SubmittedAsync(supplier, new PurchaseOrderLineInput(part, 10));
        var line = (await GetOrderAsync(order.Id)).Lines.Single();
        Assert.True((await ReceiveAsync(order.Id, [new ReceiveLineInput(line.Id, 4)])).Succeeded);
        var filter = new PurchaseOrderReportFilter();

        var report = await WithReportsAsync(s => s.GetPurchaseOrdersAsync(filter));
        var csv = CsvLines(await CsvAsync((s, stream) => s.WritePurchaseOrdersCsvAsync(filter, stream)));

        Assert.Equal(1000, report.Totals.TotalCents);
        Assert.Equal(600, report.Totals.OutstandingCents);
        Assert.Equal("Number,Supplier,Status,Order date,Expected,Lines,Total,Outstanding", csv[0]);
        var cells = csv[1].Split(',');
        Assert.Equal(order.Number, cells[0]);
        Assert.Equal("PartiallyReceived", cells[2]);
        Assert.Equal("10.00", cells[6]);
        Assert.Equal("6.00", cells[7]);
    }

    // ---- dead stock -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Dead_stock_lists_active_parts_with_stock_and_no_movement_in_the_period_longest_idle_first()
    {
        var recent = await PartAsync("RECENT", 100);
        var stale = await PartAsync("STALE", 200);
        var staler = await PartAsync("STALER", 300);
        var empty = await PartAsync("EMPTY");
        var archived = await PartAsync("ARCHIVED");
        var untouched = await PartAsync("NEVER", 400);
        Assert.True((await PostAsync(
            At(recent, StockTransactionType.Receive, 5, Ago(10)),
            At(stale, StockTransactionType.Receive, 5, Ago(100)),
            At(staler, StockTransactionType.Receive, 2, Ago(400)),
            At(empty, StockTransactionType.Receive, 5, Ago(300)),
            At(empty, StockTransactionType.Issue, 5, Ago(299)),
            At(archived, StockTransactionType.Receive, 5, Ago(300)))).Succeeded);
        await SetOnHandAsync(untouched, 3);   // stock that has no ledger history at all
        await ArchiveAsync(archived);

        var report = await WithReportsAsync(s => s.GetDeadStockAsync(new DeadStockFilter()));
        var longer = await WithReportsAsync(s => s.GetDeadStockAsync(new DeadStockFilter { Days = 200 }));

        Assert.Equal(["NEVER", "STALER", "STALE"], report.Page.Items.Select(r => r.Sku));
        Assert.Null(report.Page.Items[0].LastMovementAt);
        Assert.Equal(new DeadStockTotals(3, 3 + 2 + 5, 3 * 400 + 2 * 300 + 5 * 200), report.Totals);
        Assert.Equal(["NEVER", "STALER"], longer.Page.Items.Select(r => r.Sku));
    }

    [Fact]
    public async Task Dead_stock_days_are_clamped_and_the_csv_has_every_row()
    {
        for (var i = 1; i <= 3; i++)
        {
            var id = await PartAsync($"D{i}", 100);
            Assert.True((await PostAsync(At(id, StockTransactionType.Receive, i, Ago(500)))).Succeeded);
        }

        var filter = new DeadStockFilter { Days = -5, PageSize = 2 };
        var csv = CsvLines(await CsvAsync((s, stream) => s.WriteDeadStockCsvAsync(filter, stream)));
        var report = await WithReportsAsync(s => s.GetDeadStockAsync(filter));

        Assert.Equal(1, filter.SafeDays);
        Assert.Equal(3, report.Totals.Products);
        Assert.Equal(2, report.Page.Items.Count);
        Assert.Equal("SKU,Name,Category,Unit,On hand,Unit cost,Value,Last movement (UTC)", csv[0]);
        Assert.Equal(4, csv.Length);
    }

    [Fact]
    public async Task Cells_that_start_like_a_formula_are_neutralised_in_report_csvs()
    {
        var id = (await CreateProductAsync(Input(sku: "F-1", name: "=1+1"))).Id;
        Assert.True((await PostAsync(Receive(id, 1))).Succeeded);

        var csv = await CsvAsync((s, stream) => s.WriteValuationCsvAsync(new ValuationFilter(), stream));

        Assert.Contains("F-1,'=1+1,", csv);
    }
}
