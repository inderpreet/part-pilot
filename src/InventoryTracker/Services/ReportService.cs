using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

/// <summary>
/// The standard reports (F06 FR-7). Each has a paged on-screen form with totals, and a CSV writer that uses the same filter
/// and the same ordering but returns every row, so the file always matches what the screen shows.
/// </summary>
public interface IReportService
{
    Task<ValuationReport> GetValuationAsync(ValuationFilter filter, CancellationToken cancellationToken = default);

    Task WriteValuationCsvAsync(ValuationFilter filter, Stream destination, CancellationToken cancellationToken = default);

    Task<MovementReport> GetMovementsAsync(MovementReportFilter filter, CancellationToken cancellationToken = default);

    Task WriteMovementsCsvAsync(MovementReportFilter filter, Stream destination, CancellationToken cancellationToken = default);

    Task<UsageReport> GetUsageAsync(UsageFilter filter, CancellationToken cancellationToken = default);

    Task WriteUsageCsvAsync(UsageFilter filter, Stream destination, CancellationToken cancellationToken = default);

    Task<PurchaseOrderReport> GetPurchaseOrdersAsync(PurchaseOrderReportFilter filter, CancellationToken cancellationToken = default);

    Task WritePurchaseOrdersCsvAsync(PurchaseOrderReportFilter filter, Stream destination, CancellationToken cancellationToken = default);

    Task<DeadStockReport> GetDeadStockAsync(DeadStockFilter filter, CancellationToken cancellationToken = default);

    Task WriteDeadStockCsvAsync(DeadStockFilter filter, Stream destination, CancellationToken cancellationToken = default);
}

public sealed class ReportService(AppDbContext db, IClock clock) : IReportService
{
    // ---- stock on hand & valuation --------------------------------------------------------------------------------

    private IQueryable<Product> ValuationProducts(ValuationFilter filter)
    {
        var products = db.Products.AsNoTracking().AsQueryable();
        if (!filter.IncludeArchived)
        {
            products = products.Where(p => p.IsActive);
        }

        if (filter.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        return SearchProducts(products, filter.SearchTerm);
    }

    private IOrderedQueryable<ValuationRow> ValuationRows(ValuationFilter filter)
    {
        var rows = ValuationProducts(filter).Select(p => new ValuationRow
        {
            ProductId = p.Id,
            Sku = p.Sku,
            Name = p.Name,
            CategoryName = p.Category == null ? null : p.Category.Name,
            Unit = p.Unit,
            OnHand = p.QuantityOnHand,
            UnitCostCents = p.UnitCostCents,
            ValueCents = (long)p.QuantityOnHand * p.UnitCostCents,
            IsActive = p.IsActive,
        });

        return (filter.Sort?.ToLowerInvariant(), filter.Desc) switch
        {
            ("sku", false) => rows.OrderBy(r => r.Sku),
            ("sku", true) => rows.OrderByDescending(r => r.Sku),
            ("category", false) => rows.OrderBy(r => r.CategoryName).ThenBy(r => r.Name),
            ("category", true) => rows.OrderByDescending(r => r.CategoryName).ThenBy(r => r.Name),
            ("onhand", false) => rows.OrderBy(r => r.OnHand).ThenBy(r => r.Name),
            ("onhand", true) => rows.OrderByDescending(r => r.OnHand).ThenBy(r => r.Name),
            ("cost", false) => rows.OrderBy(r => r.UnitCostCents).ThenBy(r => r.Name),
            ("cost", true) => rows.OrderByDescending(r => r.UnitCostCents).ThenBy(r => r.Name),
            ("value", false) => rows.OrderBy(r => r.ValueCents).ThenBy(r => r.Name),
            ("value", true) => rows.OrderByDescending(r => r.ValueCents).ThenBy(r => r.Name),
            (_, true) => rows.OrderByDescending(r => r.Name),
            _ => rows.OrderBy(r => r.Name),
        };
    }

    public async Task<ValuationReport> GetValuationAsync(ValuationFilter filter, CancellationToken cancellationToken = default)
    {
        var page = await PagedResult<ValuationRow>.CreateAsync(ValuationRows(filter).ThenBy(r => r.ProductId), filter, cancellationToken);

        var byCategory = await ValuationProducts(filter)
            .GroupBy(p => new { p.CategoryId, Name = p.Category == null ? null : p.Category.Name })
            .Select(g => new CategoryValue
            {
                CategoryName = g.Key.Name,
                Products = g.Count(),
                Units = g.Sum(p => (long)p.QuantityOnHand),
                ValueCents = g.Sum(p => (long)p.QuantityOnHand * p.UnitCostCents),
            })
            .OrderByDescending(c => c.ValueCents).ThenBy(c => c.CategoryName)
            .ToListAsync(cancellationToken);

        var totals = new ValuationTotals(byCategory.Sum(c => c.Products), byCategory.Sum(c => c.Units), byCategory.Sum(c => c.ValueCents));
        return new ValuationReport(page, totals, byCategory);
    }

    public Task WriteValuationCsvAsync(ValuationFilter filter, Stream destination, CancellationToken cancellationToken = default)
        => CsvExport.WriteAsync(destination, ValuationRows(filter).ThenBy(r => r.ProductId).AsAsyncEnumerable(),
        [
            CsvColumn.Text<ValuationRow>("SKU", r => r.Sku),
            CsvColumn.Text<ValuationRow>("Name", r => r.Name),
            CsvColumn.Text<ValuationRow>("Category", r => r.CategoryName),
            CsvColumn.Text<ValuationRow>("Unit", r => r.Unit),
            CsvColumn.Int<ValuationRow>("On hand", r => r.OnHand),
            CsvColumn.Money<ValuationRow>("Unit cost", r => r.UnitCostCents),
            CsvColumn.Money<ValuationRow>("Value", r => r.ValueCents),
            CsvColumn.Text<ValuationRow>("Active", r => r.IsActive ? "Yes" : "No"),
        ], cancellationToken);

    // ---- stock movements -------------------------------------------------------------------------------------------

    private IQueryable<StockTransaction> MovementTransactions(MovementReportFilter filter)
    {
        var rows = db.StockTransactions.AsNoTracking().AsQueryable();

        if (filter.FromUtc is { } from)
        {
            rows = rows.Where(t => t.OccurredAt >= from);
        }

        if (filter.ToUtcExclusive is { } to)
        {
            rows = rows.Where(t => t.OccurredAt < to);
        }

        if (filter.Type is { } type)
        {
            rows = rows.Where(t => t.Type == type);
        }

        if (filter.CategoryId is { } categoryId)
        {
            rows = rows.Where(t => t.Product!.CategoryId == categoryId);
        }

        if (filter.ProductId is { } productId)
        {
            rows = rows.Where(t => t.ProductId == productId);
        }

        if (!string.IsNullOrEmpty(filter.UserId))
        {
            var userId = filter.UserId;
            rows = rows.Where(t => t.UserId == userId);
        }

        if (filter.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            rows = rows.Where(t =>
                EF.Functions.Like(t.Product!.Sku, pattern, LikePattern.Escape)
                || EF.Functions.Like(t.Product.Name, pattern, LikePattern.Escape)
                || (t.Reason != null && EF.Functions.Like(t.Reason, pattern, LikePattern.Escape))
                || (t.Reference != null && EF.Functions.Like(t.Reference, pattern, LikePattern.Escape)));
        }

        return rows;
    }

    private IQueryable<MovementReportRow> MovementRows(MovementReportFilter filter)
        => MovementTransactions(filter)
            .OrderByDescending(t => t.OccurredAt).ThenByDescending(t => t.Id)
            .Select(t => new MovementReportRow
            {
                Id = t.Id,
                OccurredAt = t.OccurredAt,
                ProductId = t.ProductId,
                Sku = t.Product!.Sku,
                ProductName = t.Product.Name,
                CategoryName = t.Product.Category == null ? null : t.Product.Category.Name,
                Type = t.Type,
                QuantityDelta = t.QuantityDelta,
                QuantityAfter = t.QuantityAfter,
                UnitCostCents = t.UnitCostCents,
                Reason = t.Reason,
                Reference = t.Reference,
                UserName = t.User!.DisplayName,
            });

    public async Task<MovementReport> GetMovementsAsync(MovementReportFilter filter, CancellationToken cancellationToken = default)
    {
        var page = await PagedResult<MovementReportRow>.CreateAsync(MovementRows(filter), filter, cancellationToken);

        // The count is already known from the page; the sums need one more pass over the same rows.
        var sums = await MovementTransactions(filter)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                In = g.Sum(t => t.QuantityDelta > 0 ? (long)t.QuantityDelta : 0L),
                Out = g.Sum(t => t.QuantityDelta < 0 ? -(long)t.QuantityDelta : 0L),
            })
            .FirstOrDefaultAsync(cancellationToken);

        return new MovementReport(page, new MovementTotals(page.TotalCount, sums?.In ?? 0, sums?.Out ?? 0));
    }

    public Task WriteMovementsCsvAsync(MovementReportFilter filter, Stream destination, CancellationToken cancellationToken = default)
        => CsvExport.WriteAsync(destination, MovementRows(filter).AsAsyncEnumerable(),
        [
            CsvColumn.Utc<MovementReportRow>("Date (UTC)", r => r.OccurredAt),
            CsvColumn.Text<MovementReportRow>("SKU", r => r.Sku),
            CsvColumn.Text<MovementReportRow>("Name", r => r.ProductName),
            CsvColumn.Text<MovementReportRow>("Category", r => r.CategoryName),
            CsvColumn.Text<MovementReportRow>("Type", r => r.Type.ToString()),
            CsvColumn.Int<MovementReportRow>("Quantity change", r => r.QuantityDelta),
            CsvColumn.Int<MovementReportRow>("On hand after", r => r.QuantityAfter),
            CsvColumn.Money<MovementReportRow>("Unit cost", r => r.UnitCostCents),
            CsvColumn.Text<MovementReportRow>("Reason", r => r.Reason),
            CsvColumn.Text<MovementReportRow>("Reference", r => r.Reference),
            CsvColumn.Text<MovementReportRow>("User", r => r.UserName),
        ], cancellationToken);

    // ---- usage -----------------------------------------------------------------------------------------------------

    /// <summary>
    /// All parts that had issues in the period, sorted. There is at most one row per part, so this is small enough to hold in
    /// memory, which keeps the totals, the page and the CSV to a single pass over the ledger.
    /// </summary>
    private async Task<List<UsageRow>> UsageRowsAsync(UsageFilter filter, CancellationToken cancellationToken)
    {
        var issues = db.StockTransactions.AsNoTracking().Where(t => t.Type == StockTransactionType.Issue);
        if (filter.FromUtc is { } from)
        {
            issues = issues.Where(t => t.OccurredAt >= from);
        }

        if (filter.ToUtcExclusive is { } to)
        {
            issues = issues.Where(t => t.OccurredAt < to);
        }

        var products = db.Products.AsNoTracking().AsQueryable();
        if (filter.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        products = SearchProducts(products, filter.SearchTerm);

        var rows = await (
                from g in issues.GroupBy(t => t.ProductId).Select(g => new { ProductId = g.Key, Units = -g.Sum(t => (long)t.QuantityDelta), Count = g.Count() })
                join p in products on g.ProductId equals p.Id
                select new UsageRow
                {
                    ProductId = p.Id,
                    Sku = p.Sku,
                    Name = p.Name,
                    CategoryName = p.Category == null ? null : p.Category.Name,
                    Unit = p.Unit,
                    UnitsUsed = g.Units,
                    Issues = g.Count,
                    ValueCents = g.Units * p.UnitCostCents,
                })
            .ToListAsync(cancellationToken);

        IOrderedEnumerable<UsageRow> sorted = (filter.Sort?.ToLowerInvariant(), filter.Desc) switch
        {
            ("sku", false) => rows.OrderBy(r => r.Sku, StringComparer.OrdinalIgnoreCase),
            ("sku", true) => rows.OrderByDescending(r => r.Sku, StringComparer.OrdinalIgnoreCase),
            ("name", false) => rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase),
            ("name", true) => rows.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase),
            ("category", false) => rows.OrderBy(r => r.CategoryName, StringComparer.OrdinalIgnoreCase),
            ("category", true) => rows.OrderByDescending(r => r.CategoryName, StringComparer.OrdinalIgnoreCase),
            ("issues", false) => rows.OrderBy(r => r.Issues),
            ("issues", true) => rows.OrderByDescending(r => r.Issues),
            ("value", false) => rows.OrderBy(r => r.ValueCents),
            ("value", true) => rows.OrderByDescending(r => r.ValueCents),
            ("units", false) when filter.Sort is not null => rows.OrderBy(r => r.UnitsUsed),
            _ => rows.OrderByDescending(r => r.UnitsUsed),
        };

        return sorted.ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.ProductId).ToList();
    }

    public async Task<UsageReport> GetUsageAsync(UsageFilter filter, CancellationToken cancellationToken = default)
    {
        var all = await UsageRowsAsync(filter, cancellationToken);
        return new UsageReport(PageOf(all, filter), new UsageTotals(all.Count, all.Sum(r => r.UnitsUsed), all.Sum(r => r.ValueCents)));
    }

    public async Task WriteUsageCsvAsync(UsageFilter filter, Stream destination, CancellationToken cancellationToken = default)
        => await CsvExport.WriteAsync(destination, await UsageRowsAsync(filter, cancellationToken),
        [
            CsvColumn.Text<UsageRow>("SKU", r => r.Sku),
            CsvColumn.Text<UsageRow>("Name", r => r.Name),
            CsvColumn.Text<UsageRow>("Category", r => r.CategoryName),
            CsvColumn.Text<UsageRow>("Unit", r => r.Unit),
            CsvColumn.Int<UsageRow>("Units used", r => r.UnitsUsed),
            CsvColumn.Int<UsageRow>("Issues", r => r.Issues),
            CsvColumn.Money<UsageRow>("Value at current cost", r => r.ValueCents),
        ], cancellationToken);

    // ---- purchase orders -------------------------------------------------------------------------------------------

    private IQueryable<PurchaseOrder> PurchaseOrders(PurchaseOrderReportFilter filter)
    {
        var orders = db.PurchaseOrders.AsNoTracking().AsQueryable();

        if (filter.From is { } from)
        {
            orders = orders.Where(o => o.OrderDate >= from);
        }

        if (filter.To is { } to)
        {
            orders = orders.Where(o => o.OrderDate <= to);
        }

        if (filter.SupplierId is { } supplierId)
        {
            orders = orders.Where(o => o.SupplierId == supplierId);
        }

        if (filter.Status is { } status)
        {
            orders = orders.Where(o => o.Status == status);
        }

        if (filter.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            orders = orders.Where(o => EF.Functions.Like(o.Number, pattern, LikePattern.Escape));
        }

        return orders;
    }

    private IOrderedQueryable<PurchaseOrderReportRow> PurchaseOrderRows(PurchaseOrderReportFilter filter)
    {
        var open = ReportingQueries.OpenStatuses;
        var rows = PurchaseOrders(filter).Select(o => new PurchaseOrderReportRow
        {
            Id = o.Id,
            Number = o.Number,
            SupplierId = o.SupplierId,
            SupplierName = o.Supplier!.Name,
            Status = o.Status,
            OrderDate = o.OrderDate,
            ExpectedDate = o.ExpectedDate,
            Lines = o.Lines.Count,
            TotalCents = o.Lines.Sum(l => (long)l.QuantityOrdered * l.UnitCostCents),
            OutstandingCents = open.Contains(o.Status)
                ? o.Lines.Where(l => l.QuantityOrdered > l.QuantityReceived).Sum(l => (long)(l.QuantityOrdered - l.QuantityReceived) * l.UnitCostCents)
                : 0L,
        });

        return (filter.Sort?.ToLowerInvariant(), filter.Desc) switch
        {
            ("number", false) => rows.OrderBy(r => r.Number),
            ("number", true) => rows.OrderByDescending(r => r.Number),
            ("supplier", false) => rows.OrderBy(r => r.SupplierName).ThenByDescending(r => r.OrderDate),
            ("supplier", true) => rows.OrderByDescending(r => r.SupplierName).ThenByDescending(r => r.OrderDate),
            ("status", false) => rows.OrderBy(r => r.Status).ThenByDescending(r => r.OrderDate),
            ("status", true) => rows.OrderByDescending(r => r.Status).ThenByDescending(r => r.OrderDate),
            ("expected", false) => rows.OrderBy(r => r.ExpectedDate == null).ThenBy(r => r.ExpectedDate),
            ("expected", true) => rows.OrderByDescending(r => r.ExpectedDate),
            ("total", false) => rows.OrderBy(r => r.TotalCents).ThenByDescending(r => r.OrderDate),
            ("total", true) => rows.OrderByDescending(r => r.TotalCents).ThenByDescending(r => r.OrderDate),
            ("outstanding", false) => rows.OrderBy(r => r.OutstandingCents).ThenByDescending(r => r.OrderDate),
            ("outstanding", true) => rows.OrderByDescending(r => r.OutstandingCents).ThenByDescending(r => r.OrderDate),
            ("date", false) when filter.Sort is not null => rows.OrderBy(r => r.OrderDate),
            _ => rows.OrderByDescending(r => r.OrderDate).ThenByDescending(r => r.Id),
        };
    }

    public async Task<PurchaseOrderReport> GetPurchaseOrdersAsync(PurchaseOrderReportFilter filter, CancellationToken cancellationToken = default)
    {
        var page = await PagedResult<PurchaseOrderReportRow>.CreateAsync(PurchaseOrderRows(filter).ThenBy(r => r.Id), filter, cancellationToken);

        var open = ReportingQueries.OpenStatuses;
        var bySupplier = await PurchaseOrders(filter)
            .GroupBy(o => new { o.SupplierId, o.Supplier!.Name })
            .Select(g => new SupplierOrderSummary
            {
                SupplierId = g.Key.SupplierId,
                SupplierName = g.Key.Name,
                Orders = g.Count(),
                TotalCents = g.Sum(o => o.Lines.Sum(l => (long)l.QuantityOrdered * l.UnitCostCents)),
                OutstandingCents = g.Sum(o => open.Contains(o.Status)
                    ? o.Lines.Where(l => l.QuantityOrdered > l.QuantityReceived).Sum(l => (long)(l.QuantityOrdered - l.QuantityReceived) * l.UnitCostCents)
                    : 0L),
            })
            .OrderByDescending(s => s.TotalCents).ThenBy(s => s.SupplierName)
            .ToListAsync(cancellationToken);

        var totals = new PurchaseOrderTotals(bySupplier.Sum(s => s.Orders), bySupplier.Sum(s => s.TotalCents), bySupplier.Sum(s => s.OutstandingCents));
        return new PurchaseOrderReport(page, totals, bySupplier);
    }

    public Task WritePurchaseOrdersCsvAsync(PurchaseOrderReportFilter filter, Stream destination, CancellationToken cancellationToken = default)
        => CsvExport.WriteAsync(destination, PurchaseOrderRows(filter).ThenBy(r => r.Id).AsAsyncEnumerable(),
        [
            CsvColumn.Text<PurchaseOrderReportRow>("Number", r => r.Number),
            CsvColumn.Text<PurchaseOrderReportRow>("Supplier", r => r.SupplierName),
            CsvColumn.Text<PurchaseOrderReportRow>("Status", r => r.Status.ToString()),
            CsvColumn.Date<PurchaseOrderReportRow>("Order date", r => r.OrderDate),
            CsvColumn.Date<PurchaseOrderReportRow>("Expected", r => r.ExpectedDate),
            CsvColumn.Int<PurchaseOrderReportRow>("Lines", r => r.Lines),
            CsvColumn.Money<PurchaseOrderReportRow>("Total", r => r.TotalCents),
            CsvColumn.Money<PurchaseOrderReportRow>("Outstanding", r => r.OutstandingCents),
        ], cancellationToken);

    // ---- dead stock ------------------------------------------------------------------------------------------------

    private IQueryable<Product> DeadProducts(DeadStockFilter filter)
    {
        var cutoff = clock.UtcNow.AddDays(-filter.SafeDays);
        var products = db.Products.AsNoTracking()
            .Where(p => p.IsActive && p.QuantityOnHand > 0
                        && !db.StockTransactions.Any(t => t.ProductId == p.Id && t.OccurredAt >= cutoff));

        if (filter.CategoryId is { } categoryId)
        {
            products = products.Where(p => p.CategoryId == categoryId);
        }

        return SearchProducts(products, filter.SearchTerm);
    }

    private IOrderedQueryable<DeadStockRow> DeadStockRows(DeadStockFilter filter)
    {
        var rows = DeadProducts(filter).Select(p => new DeadStockRow
        {
            ProductId = p.Id,
            Sku = p.Sku,
            Name = p.Name,
            CategoryName = p.Category == null ? null : p.Category.Name,
            Unit = p.Unit,
            OnHand = p.QuantityOnHand,
            UnitCostCents = p.UnitCostCents,
            ValueCents = (long)p.QuantityOnHand * p.UnitCostCents,
            LastMovementAt = db.StockTransactions.Where(t => t.ProductId == p.Id).Max(t => (DateTime?)t.OccurredAt),
        });

        return (filter.Sort?.ToLowerInvariant(), filter.Desc) switch
        {
            ("sku", false) => rows.OrderBy(r => r.Sku),
            ("sku", true) => rows.OrderByDescending(r => r.Sku),
            ("name", false) => rows.OrderBy(r => r.Name),
            ("name", true) => rows.OrderByDescending(r => r.Name),
            ("category", false) => rows.OrderBy(r => r.CategoryName).ThenBy(r => r.Name),
            ("category", true) => rows.OrderByDescending(r => r.CategoryName).ThenBy(r => r.Name),
            ("onhand", false) => rows.OrderBy(r => r.OnHand).ThenBy(r => r.Name),
            ("onhand", true) => rows.OrderByDescending(r => r.OnHand).ThenBy(r => r.Name),
            ("value", false) => rows.OrderBy(r => r.ValueCents).ThenBy(r => r.Name),
            ("value", true) => rows.OrderByDescending(r => r.ValueCents).ThenBy(r => r.Name),

            // Default: longest idle first (a part that never moved sorts before everything else).
            (_, true) => rows.OrderByDescending(r => r.LastMovementAt).ThenBy(r => r.Name),
            _ => rows.OrderBy(r => r.LastMovementAt).ThenBy(r => r.Name),
        };
    }

    public async Task<DeadStockReport> GetDeadStockAsync(DeadStockFilter filter, CancellationToken cancellationToken = default)
    {
        var page = await PagedResult<DeadStockRow>.CreateAsync(DeadStockRows(filter).ThenBy(r => r.ProductId), filter, cancellationToken);

        var totals = await DeadProducts(filter)
            .GroupBy(_ => 1)
            .Select(g => new DeadStockTotals(g.Count(), g.Sum(p => (long)p.QuantityOnHand), g.Sum(p => (long)p.QuantityOnHand * p.UnitCostCents)))
            .FirstOrDefaultAsync(cancellationToken);

        return new DeadStockReport(page, totals ?? new DeadStockTotals(0, 0, 0));
    }

    public Task WriteDeadStockCsvAsync(DeadStockFilter filter, Stream destination, CancellationToken cancellationToken = default)
        => CsvExport.WriteAsync(destination, DeadStockRows(filter).ThenBy(r => r.ProductId).AsAsyncEnumerable(),
        [
            CsvColumn.Text<DeadStockRow>("SKU", r => r.Sku),
            CsvColumn.Text<DeadStockRow>("Name", r => r.Name),
            CsvColumn.Text<DeadStockRow>("Category", r => r.CategoryName),
            CsvColumn.Text<DeadStockRow>("Unit", r => r.Unit),
            CsvColumn.Int<DeadStockRow>("On hand", r => r.OnHand),
            CsvColumn.Money<DeadStockRow>("Unit cost", r => r.UnitCostCents),
            CsvColumn.Money<DeadStockRow>("Value", r => r.ValueCents),
            CsvColumn.Utc<DeadStockRow>("Last movement (UTC)", r => r.LastMovementAt),
        ], cancellationToken);

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static IQueryable<Product> SearchProducts(IQueryable<Product> products, string? term)
    {
        if (term is null)
        {
            return products;
        }

        var pattern = LikePattern.Contains(term);
        return products.Where(p => EF.Functions.Like(p.Sku, pattern, LikePattern.Escape) || EF.Functions.Like(p.Name, pattern, LikePattern.Escape));
    }

    /// <summary>One page of an already sorted list, snapping to the last page like <see cref="PagedResult{T}.CreateAsync"/>.</summary>
    private static PagedResult<T> PageOf<T>(IReadOnlyList<T> all, ListQuery query)
    {
        var size = query.SafePageSize;
        var lastPage = Math.Max(1, (int)Math.Ceiling(all.Count / (double)size));
        var page = Math.Min(query.SafePage, lastPage);
        return new PagedResult<T>(all.Skip((page - 1) * size).Take(size).ToList(), page, size, all.Count);
    }
}
