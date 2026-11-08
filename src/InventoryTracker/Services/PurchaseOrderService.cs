using System.Globalization;
using System.Linq.Expressions;
using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface IPurchaseOrderService
{
    // ---- queries -----------------------------------------------------------------------------------------------

    Task<PagedResult<PurchaseOrderListItem>> SearchAsync(PurchaseOrderListQuery query, CancellationToken cancellationToken = default);

    /// <summary>The order with its lines (in the order they were added), or <c>null</c>.</summary>
    Task<PurchaseOrderDetail?> GetAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>Stock receipts and reversals posted against the order's lines, newest first.</summary>
    Task<IReadOnlyList<PurchaseOrderReceiptItem>> GetReceiptsAsync(int purchaseOrderId, CancellationToken cancellationToken = default);

    /// <summary>Lines of submitted / partially received orders that still have something to arrive for the product, soonest expected first.</summary>
    Task<IReadOnlyList<OpenOrderLineItem>> GetOpenLinesForProductAsync(int productId, CancellationToken cancellationToken = default);

    /// <summary>Σ outstanding per product over submitted / partially received orders (the "on order" quantity, F06). Products with nothing on order are absent.</summary>
    Task<IReadOnlyDictionary<int, int>> GetOnOrderByProductAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Σ quantity per product over the lines of <b>all Draft orders</b>, with the orders that hold it. Cancelled, submitted and
    /// later orders are never included. Products in no draft are absent. Pass <paramref name="productIds"/> to limit the query.
    /// </summary>
    Task<IReadOnlyDictionary<int, InDraftQuantity>> GetInDraftByProductAsync(
        IReadOnlyCollection<int>? productIds = null, CancellationToken cancellationToken = default);

    /// <summary>A supplier's most recent orders (by order date), for the supplier page.</summary>
    Task<IReadOnlyList<PurchaseOrderListItem>> GetRecentForSupplierAsync(int supplierId, int take = 10, CancellationToken cancellationToken = default);

    /// <summary>Products to offer when adding a line to an order for the supplier: its linked products first. Archived products are left out.</summary>
    Task<IReadOnlyList<PurchaseOrderProductChoice>> LookupProductsAsync(
        int supplierId, string? query, int? purchaseOrderId = null, int take = 20, CancellationToken cancellationToken = default);

    // ---- numbering ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Issues the next number for <paramref name="year"/> (<c>PO-2026-0001</c>). Inside the caller's transaction when one is
    /// open, so a failed save hands the number back; a number whose order was later deleted is never issued again.
    /// </summary>
    Task<string> NextNumberAsync(int year, CancellationToken cancellationToken = default);

    // ---- drafts ------------------------------------------------------------------------------------------------

    Task<Result<PurchaseOrderDetail>> CreateDraftAsync(
        int supplierId,
        PurchaseOrderActor actor,
        IReadOnlyList<PurchaseOrderLineInput>? lines = null,
        DateOnly? orderDate = null,
        CancellationToken cancellationToken = default);

    /// <summary>Saves the header if <paramref name="rowVersion"/> (the version the form was loaded with) is still current.</summary>
    Task<Result<PurchaseOrderDetail>> UpdateHeaderAsync(int id, long rowVersion, PurchaseOrderHeaderInput input, CancellationToken cancellationToken = default);

    /// <summary>Adds a line to a draft. A <c>null</c> cost takes the supplier link's price (when above zero), else the product's.</summary>
    Task<Result<PurchaseOrderLineItem>> AddLineAsync(
        int purchaseOrderId, int productId, int quantity, long? unitCostCents = null, CancellationToken cancellationToken = default);

    Task<Result<PurchaseOrderLineItem>> UpdateLineAsync(
        int purchaseOrderId, long lineId, int quantity, long unitCostCents, CancellationToken cancellationToken = default);

    /// <summary>Removes a line from a draft. Idempotent for a line that is already gone.</summary>
    Task<Result> RemoveLineAsync(int purchaseOrderId, long lineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies several line changes to Draft orders in one transaction (the BOM shortage review uses it). Every change is
    /// checked first: a missing order, an order that is not a Draft (<c>po.not_editable</c>) or a quantity change for a line the
    /// order does not hold (<c>po.line_not_found</c>) refuses the whole call and applies nothing. An add that cannot be made
    /// (already on that order, archived or missing product, quantity below 1) is skipped with a reason instead. Added lines take
    /// the supplier link's price when above zero, else the product's cost; changed lines keep their cost. The outcomes come back
    /// in the order of <paramref name="changes"/>.
    /// </summary>
    Task<Result<IReadOnlyList<DraftChangeOutcome>>> ApplyDraftChangesAsync(
        IReadOnlyList<DraftLineChange> changes, CancellationToken cancellationToken = default);

    // ---- transitions -------------------------------------------------------------------------------------------

    /// <summary>Draft to Submitted. Needs at least one line, an active supplier and active products; fills a missing expected date from the lead times.</summary>
    Task<Result<PurchaseOrderDetail>> SubmitAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Draft or Submitted to Cancelled.</summary>
    Task<Result> CancelAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft (its number is not reused).</summary>
    Task<Result> DeleteDraftAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default);

    /// <summary>PartiallyReceived to Received: the rest will not arrive.</summary>
    Task<Result> CloseShortAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default);

    // ---- receiving ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Receives goods: one <c>Receive</c> stock transaction per line with a quantity above zero (cost = the line's cost), the
    /// lines' received quantities and the order's status, all in one database transaction. Outstanding quantities are re-read
    /// inside it. More than outstanding is refused unless <paramref name="allowOverReceipt"/> is set <em>and</em> the actor is an admin.
    /// </summary>
    Task<Result<ReceiptResult>> ReceiveAsync(
        int purchaseOrderId,
        IReadOnlyList<ReceiveLineInput> lines,
        PurchaseOrderActor actor,
        string? reference = null,
        string? note = null,
        bool allowOverReceipt = false,
        long? rowVersion = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reverses a receipt: posts the opposite stock transaction, lowers the line's received quantity and re-derives the status
    /// (a received order is reopened). The generic stock reverse refuses these rows.
    /// </summary>
    Task<Result<PostedMovement>> ReverseReceiptAsync(
        long transactionId, PurchaseOrderActor actor, string? note = null, CancellationToken cancellationToken = default);
}

public sealed class PurchaseOrderService(
    AppDbContext db,
    IStockService stock,
    ISettingsService settings,
    IClock clock,
    PurchaseOrderEventPublisher events) : IPurchaseOrderService
{
    /// <summary>Name of the numbering series in <see cref="DocumentSequence"/>.</summary>
    public const string SequenceName = "PurchaseOrder";

    public const int MaxQuantity = StockService.MaxQuantity;

    /// <summary>Largest unit cost, in minor units. Keeps a line total (quantity × cost) inside a <see cref="long"/>.</summary>
    public const long MaxUnitCostCents = 100_000_000;

    public const int MaxLookupTake = 50;

    // ---- queries -----------------------------------------------------------------------------------------------

    private static readonly Expression<Func<PurchaseOrder, long>> TotalOf =
        o => o.Lines.Sum(l => (long)l.QuantityOrdered * l.UnitCostCents);

    private static readonly Expression<Func<PurchaseOrder, long>> OutstandingOf =
        o => o.Status == PurchaseOrderStatus.Submitted || o.Status == PurchaseOrderStatus.PartiallyReceived
            ? o.Lines.Sum(l => l.QuantityOrdered > l.QuantityReceived ? (long)(l.QuantityOrdered - l.QuantityReceived) * l.UnitCostCents : 0L)
            : 0L;

    public async Task<PagedResult<PurchaseOrderListItem>> SearchAsync(PurchaseOrderListQuery query, CancellationToken cancellationToken = default)
    {
        var orders = db.PurchaseOrders.AsNoTracking().AsQueryable();

        orders = query.Tab switch
        {
            PurchaseOrderTab.Open => orders.Where(o => o.Status == PurchaseOrderStatus.Submitted || o.Status == PurchaseOrderStatus.PartiallyReceived),
            PurchaseOrderTab.Draft => orders.Where(o => o.Status == PurchaseOrderStatus.Draft),
            PurchaseOrderTab.Received => orders.Where(o => o.Status == PurchaseOrderStatus.Received),
            PurchaseOrderTab.Cancelled => orders.Where(o => o.Status == PurchaseOrderStatus.Cancelled),
            _ => orders,
        };

        if (query.SupplierId is { } supplierId)
        {
            orders = orders.Where(o => o.SupplierId == supplierId);
        }

        if (query.From is { } from)
        {
            orders = orders.Where(o => o.OrderDate >= from);
        }

        if (query.To is { } to)
        {
            orders = orders.Where(o => o.OrderDate <= to);
        }

        if (query.SearchTerm is { } term)
        {
            var pattern = LikePattern.Contains(term);
            orders = orders.Where(o => EF.Functions.Like(o.Number, pattern, LikePattern.Escape));
        }

        // Every ordering ends in Id so paging is stable when the sort column has ties.
        var ordered = (query.Sort?.ToLowerInvariant(), query.Desc) switch
        {
            ("number", false) => orders.OrderBy(o => o.Number),
            ("number", true) => orders.OrderByDescending(o => o.Number),
            ("supplier", false) => orders.OrderBy(o => o.Supplier!.Name).ThenByDescending(o => o.OrderDate),
            ("supplier", true) => orders.OrderByDescending(o => o.Supplier!.Name).ThenByDescending(o => o.OrderDate),
            ("status", false) => orders.OrderBy(o => o.Status).ThenByDescending(o => o.OrderDate),
            ("status", true) => orders.OrderByDescending(o => o.Status).ThenByDescending(o => o.OrderDate),
            ("date", false) => orders.OrderBy(o => o.OrderDate),
            ("expected", false) => orders.OrderBy(o => o.ExpectedDate == null).ThenBy(o => o.ExpectedDate),
            ("expected", true) => orders.OrderByDescending(o => o.ExpectedDate == null).ThenByDescending(o => o.ExpectedDate),
            ("total", false) => orders.OrderBy(TotalOf),
            ("total", true) => orders.OrderByDescending(TotalOf),
            ("outstanding", false) => orders.OrderBy(OutstandingOf),
            ("outstanding", true) => orders.OrderByDescending(OutstandingOf),
            _ => orders.OrderByDescending(o => o.OrderDate),
        };

        var page = ordered.ThenByDescending(o => o.Id).Select(o => new PurchaseOrderListItem(
            o.Id,
            o.Number,
            o.SupplierId,
            o.Supplier!.Name,
            o.Status,
            o.OrderDate,
            o.ExpectedDate,
            o.Lines.Count,
            o.Lines.Sum(l => (long)l.QuantityOrdered * l.UnitCostCents),
            o.Status == PurchaseOrderStatus.Submitted || o.Status == PurchaseOrderStatus.PartiallyReceived
                ? o.Lines.Sum(l => l.QuantityOrdered > l.QuantityReceived ? (long)(l.QuantityOrdered - l.QuantityReceived) * l.UnitCostCents : 0L)
                : 0L));

        return await PagedResult<PurchaseOrderListItem>.CreateAsync(page, query, cancellationToken);
    }

    public async Task<PurchaseOrderDetail?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        var header = await db.PurchaseOrders.AsNoTracking()
            .Where(o => o.Id == id)
            .Select(o => new
            {
                o.Id,
                o.Number,
                o.Status,
                o.SupplierId,
                SupplierName = o.Supplier!.Name,
                SupplierIsActive = o.Supplier.IsActive,
                SupplierContactName = o.Supplier.ContactName,
                SupplierEmail = o.Supplier.Email,
                SupplierPhone = o.Supplier.Phone,
                SupplierAddress = o.Supplier.Address,
                o.OrderDate,
                o.ExpectedDate,
                o.SubmittedAt,
                o.ClosedAt,
                o.Notes,
                o.CreatedById,
                CreatedByName = o.CreatedBy!.DisplayName,
                o.RowVersion,
                o.CreatedAt,
                o.UpdatedAt,
            })
            .FirstOrDefaultAsync(cancellationToken);
        if (header is null)
        {
            return null;
        }

        var lines = await LineItems(db.PurchaseOrderLines.AsNoTracking().Where(l => l.PurchaseOrderId == id).OrderBy(l => l.Id), header.SupplierId)
            .ToListAsync(cancellationToken);

        return new PurchaseOrderDetail(
            header.Id, header.Number, header.Status, header.SupplierId, header.SupplierName, header.SupplierIsActive,
            header.SupplierContactName, header.SupplierEmail, header.SupplierPhone, header.SupplierAddress,
            header.OrderDate, header.ExpectedDate, header.SubmittedAt, header.ClosedAt, header.Notes,
            header.CreatedById, header.CreatedByName, header.RowVersion, header.CreatedAt, header.UpdatedAt, lines);
    }

    public async Task<IReadOnlyList<PurchaseOrderReceiptItem>> GetReceiptsAsync(int purchaseOrderId, CancellationToken cancellationToken = default)
    {
        var status = await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == purchaseOrderId).Select(o => (PurchaseOrderStatus?)o.Status).FirstOrDefaultAsync(cancellationToken);
        var open = status is { } s && s is PurchaseOrderStatus.Submitted or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received;

        var rows = await db.StockTransactions.AsNoTracking()
            .Where(t => t.PurchaseOrderLine!.PurchaseOrderId == purchaseOrderId)
            .OrderByDescending(t => t.Id)
            .Select(t => new
            {
                t.Id,
                LineId = t.PurchaseOrderLineId!.Value,
                t.ProductId,
                t.Product!.Sku,
                ProductName = t.Product.Name,
                t.Type,
                t.QuantityDelta,
                t.UnitCostCents,
                t.Reference,
                t.Note,
                UserName = t.User!.DisplayName,
                t.OccurredAt,
                IsReversed = db.StockTransactions.Any(r => r.ReversesTransactionId == t.Id),
            })
            .ToListAsync(cancellationToken);

        return rows.Select(r => new PurchaseOrderReceiptItem(
                r.Id, r.LineId, r.ProductId, r.Sku, r.ProductName, r.Type, r.QuantityDelta, r.UnitCostCents, r.Reference, r.Note,
                r.UserName, r.OccurredAt, r.IsReversed, CanReverse: open && r.Type == StockTransactionType.Receive && !r.IsReversed))
            .ToList();
    }

    public async Task<IReadOnlyList<OpenOrderLineItem>> GetOpenLinesForProductAsync(int productId, CancellationToken cancellationToken = default)
        => await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => l.ProductId == productId
                && (l.PurchaseOrder!.Status == PurchaseOrderStatus.Submitted || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived)
                && l.QuantityOrdered > l.QuantityReceived)
            .OrderBy(l => l.PurchaseOrder!.ExpectedDate == null)
            .ThenBy(l => l.PurchaseOrder!.ExpectedDate)
            .ThenBy(l => l.PurchaseOrder!.Number)
            .Select(l => new OpenOrderLineItem(
                l.PurchaseOrderId,
                l.PurchaseOrder!.Number,
                l.PurchaseOrder.SupplierId,
                l.PurchaseOrder.Supplier!.Name,
                l.PurchaseOrder.Status,
                l.PurchaseOrder.ExpectedDate,
                l.QuantityOrdered,
                l.QuantityReceived))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyDictionary<int, int>> GetOnOrderByProductAsync(CancellationToken cancellationToken = default)
    {
        var rows = await db.PurchaseOrderLines.AsNoTracking()
            .Where(l => (l.PurchaseOrder!.Status == PurchaseOrderStatus.Submitted || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived)
                && l.QuantityOrdered > l.QuantityReceived)
            .GroupBy(l => l.ProductId)
            .Select(g => new { ProductId = g.Key, OnOrder = g.Sum(l => l.QuantityOrdered - l.QuantityReceived) })
            .ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.ProductId, r => r.OnOrder);
    }

    public async Task<IReadOnlyDictionary<int, InDraftQuantity>> GetInDraftByProductAsync(
        IReadOnlyCollection<int>? productIds = null, CancellationToken cancellationToken = default)
    {
        var lines = db.PurchaseOrderLines.AsNoTracking().Where(l => l.PurchaseOrder!.Status == PurchaseOrderStatus.Draft);
        if (productIds is not null)
        {
            if (productIds.Count == 0)
            {
                return new Dictionary<int, InDraftQuantity>();
            }

            var ids = productIds.Distinct().ToList();
            lines = lines.Where(l => ids.Contains(l.ProductId));
        }

        var rows = await lines
            .OrderBy(l => l.PurchaseOrder!.Number)
            .Select(l => new { l.ProductId, l.PurchaseOrderId, l.PurchaseOrder!.Number, l.QuantityOrdered })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.ProductId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var orders = g.GroupBy(r => (r.PurchaseOrderId, r.Number))
                        .Select(o => new InDraftEntry(o.Key.PurchaseOrderId, o.Key.Number, o.Sum(r => r.QuantityOrdered)))
                        .ToList();
                    return new InDraftQuantity(orders.Sum(o => o.Quantity), orders);
                });
    }

    public async Task<IReadOnlyList<PurchaseOrderListItem>> GetRecentForSupplierAsync(int supplierId, int take = 10, CancellationToken cancellationToken = default)
    {
        var page = await SearchAsync(
            new PurchaseOrderListQuery { SupplierId = supplierId, PageSize = Math.Clamp(take, 1, 50), Page = 1 },
            cancellationToken);
        return page.Items;
    }

    public async Task<IReadOnlyList<PurchaseOrderProductChoice>> LookupProductsAsync(
        int supplierId, string? query, int? purchaseOrderId = null, int take = 20, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, MaxLookupTake);
        var products = db.Products.AsNoTracking().Where(p => p.IsActive);

        if (!string.IsNullOrWhiteSpace(query))
        {
            var pattern = LikePattern.Contains(query.Trim());
            products = products.Where(p =>
                EF.Functions.Like(p.Sku, pattern, LikePattern.Escape)
                || EF.Functions.Like(p.Name, pattern, LikePattern.Escape)
                || (p.Barcode != null && EF.Functions.Like(p.Barcode, pattern, LikePattern.Escape)));
        }

        var chosen = await products
            .OrderByDescending(p => p.Suppliers.Any(l => l.SupplierId == supplierId))
            .ThenBy(p => p.Name)
            .ThenBy(p => p.Id)
            .Take(take)
            .Select(p => new { p.Id, p.Sku, p.Name, p.Unit, p.UnitCostCents })
            .ToListAsync(cancellationToken);

        var ids = chosen.Select(p => p.Id).ToList();
        var links = await db.ProductSuppliers.AsNoTracking()
            .Where(l => l.SupplierId == supplierId && ids.Contains(l.ProductId))
            .ToDictionaryAsync(l => l.ProductId, cancellationToken);
        var onOrder = purchaseOrderId is { } poId
            ? (await db.PurchaseOrderLines.AsNoTracking().Where(l => l.PurchaseOrderId == poId && ids.Contains(l.ProductId)).Select(l => l.ProductId).ToListAsync(cancellationToken)).ToHashSet()
            : [];

        return chosen.Select(p =>
            {
                links.TryGetValue(p.Id, out var link);
                return new PurchaseOrderProductChoice(
                    p.Id, p.Sku, p.Name, p.Unit, link is not null, link?.SupplierSku, DefaultCost(link?.UnitCostCents, p.UnitCostCents),
                    link?.MinOrderQuantity, onOrder.Contains(p.Id));
            })
            .ToList();
    }

    // ---- numbering ---------------------------------------------------------------------------------------------

    public async Task<string> NextNumberAsync(int year, CancellationToken cancellationToken = default)
    {
        var prefix = await settings.GetStringAsync(SettingKeys.PurchaseOrderNumberPrefix, cancellationToken);
        if (db.Database.CurrentTransaction is not null)
        {
            return await IssueNumberAsync(prefix, year, cancellationToken);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var number = await IssueNumberAsync(prefix, year, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return number;
    }

    /// <summary>
    /// Takes the next value of the year's counter. The increment is a single UPDATE so it holds the write lock before the value
    /// is read back; the first number of a year inserts the counter row.
    /// </summary>
    private async Task<string> IssueNumberAsync(string prefix, int year, CancellationToken cancellationToken)
    {
        var bumped = await db.DocumentSequences
            .Where(s => s.Name == SequenceName && s.Year == year)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.NextValue, x => x.NextValue + 1), cancellationToken);

        int value;
        if (bumped == 0)
        {
            var sequence = new DocumentSequence { Name = SequenceName, Year = year, NextValue = 2 };
            db.DocumentSequences.Add(sequence);
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(sequence).State = EntityState.Detached;
            value = 1;
        }
        else
        {
            value = await db.DocumentSequences
                .Where(s => s.Name == SequenceName && s.Year == year)
                .Select(s => s.NextValue)
                .SingleAsync(cancellationToken) - 1;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{prefix}{year:D4}-{value:D4}");
    }

    // ---- drafts ------------------------------------------------------------------------------------------------

    public async Task<Result<PurchaseOrderDetail>> CreateDraftAsync(
        int supplierId,
        PurchaseOrderActor actor,
        IReadOnlyList<PurchaseOrderLineInput>? lines = null,
        DateOnly? orderDate = null,
        CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        lines ??= [];

        var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == supplierId).Select(s => new { s.IsActive }).FirstOrDefaultAsync(cancellationToken);
        if (supplier is null)
        {
            return Result.Failure<PurchaseOrderDetail>("po.supplier_missing", "Choose a supplier.", PurchaseOrderFields.SupplierId);
        }

        if (!supplier.IsActive)
        {
            return Result.Failure<PurchaseOrderDetail>(SupplierArchived());
        }

        var date = orderDate ?? Today();
        var errors = new List<Error>();
        for (var i = 0; i < lines.Count; i++)
        {
            ValidateQuantityAndCost(lines[i].Quantity, lines[i].UnitCostCents, PurchaseOrderFields.Line(i, PurchaseOrderFields.Quantity), PurchaseOrderFields.Line(i, PurchaseOrderFields.UnitCost), errors);
        }

        if (lines.GroupBy(l => l.ProductId).Any(g => g.Count() > 1))
        {
            errors.Add(new Error("po.line_duplicate", "A part can be on an order only once. Combine its quantities.", PurchaseOrderFields.Lines));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<PurchaseOrderDetail>([.. errors]);
        }

        // Read before the write transaction opens: the settings service uses its own connection.
        var prefix = await settings.GetStringAsync(SettingKeys.PurchaseOrderNumberPrefix, cancellationToken);

        var result = await InTransactionAsync<Result<int>>(
            async () =>
            {
                var costs = await ResolveCostsAsync(supplierId, lines.Select(l => l.ProductId), cancellationToken);
                var lineEntities = new List<PurchaseOrderLine>(lines.Count);
                for (var i = 0; i < lines.Count; i++)
                {
                    var input = lines[i];
                    if (!costs.TryGetValue(input.ProductId, out var cost))
                    {
                        return Result.Failure<int>("po.product_missing", "That part no longer exists.", PurchaseOrderFields.Line(i, PurchaseOrderFields.ProductId));
                    }

                    if (!cost.IsActive)
                    {
                        return Result.Failure<int>(ProductArchived(cost.Sku, PurchaseOrderFields.Line(i, PurchaseOrderFields.ProductId)));
                    }

                    var unitCost = input.UnitCostCents ?? DefaultCost(cost.LinkCostCents, cost.ProductCostCents);
                    if (unitCost > MaxUnitCostCents)
                    {
                        return Result.Failure<int>(CostTooLarge(PurchaseOrderFields.Line(i, PurchaseOrderFields.UnitCost)));
                    }

                    lineEntities.Add(new PurchaseOrderLine { ProductId = input.ProductId, QuantityOrdered = input.Quantity, UnitCostCents = unitCost });
                }

                var number = await IssueNumberAsync(prefix, date.Year, cancellationToken);
                var order = new PurchaseOrder
                {
                    Number = number,
                    SupplierId = supplierId,
                    OrderDate = date,
                    CreatedById = actor.UserId,
                    Lines = lineEntities,
                };
                db.PurchaseOrders.Add(order);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success(order.Id);
            },
            Conflict<int>,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Result.Failure<PurchaseOrderDetail>([.. result.Errors]);
        }

        var created = (await GetAsync(result.Value, cancellationToken))!;
        await events.PublishAsync(Event(PurchaseOrderEventType.Created, created, actor, $"{created.Lines.Count} line(s)"), cancellationToken);
        return Result.Success(created);
    }

    public async Task<Result<PurchaseOrderDetail>> UpdateHeaderAsync(
        int id, long rowVersion, PurchaseOrderHeaderInput input, CancellationToken cancellationToken = default)
    {
        var notes = Clean(input.Notes);
        var errors = new List<Error>();
        if (notes is { Length: > PurchaseOrder.NotesMaxLength })
        {
            errors.Add(new Error("po.notes_too_long", $"Notes can be at most {PurchaseOrder.NotesMaxLength} characters.", PurchaseOrderFields.Notes));
        }

        if (input.ExpectedDate is { } expected && expected < input.OrderDate)
        {
            errors.Add(new Error("po.expected_before_order", "The expected date cannot be before the order date.", PurchaseOrderFields.ExpectedDate));
        }

        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var order = await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
                if (order is null)
                {
                    return Result.Failure(NotFound());
                }

                if (order.RowVersion != rowVersion)
                {
                    return Result.Failure(ConflictError());
                }

                if (!PurchaseOrderRules.CanEditHeader(order.Status))
                {
                    return Result.Failure(Closed(order));
                }

                var local = new List<Error>(errors);
                if (PurchaseOrderRules.CanEditLines(order.Status))
                {
                    if (input.SupplierId != order.SupplierId)
                    {
                        if (order.Lines.Count > 0)
                        {
                            local.Add(new Error("po.supplier_locked", "The supplier cannot change once the order has lines. Remove the lines first.", PurchaseOrderFields.SupplierId));
                        }
                        else
                        {
                            var supplier = await db.Suppliers.AsNoTracking().Where(s => s.Id == input.SupplierId).Select(s => new { s.IsActive }).FirstOrDefaultAsync(cancellationToken);
                            if (supplier is null)
                            {
                                local.Add(new Error("po.supplier_missing", "Choose a supplier.", PurchaseOrderFields.SupplierId));
                            }
                            else if (!supplier.IsActive)
                            {
                                local.Add(SupplierArchived());
                            }
                        }
                    }
                }
                else if (input.SupplierId != order.SupplierId || input.OrderDate != order.OrderDate)
                {
                    local.Add(new Error("po.header_locked", "Only the expected date and notes can be changed once an order is submitted.", PurchaseOrderFields.SupplierId));
                }

                if (local.Count > 0)
                {
                    return Result.Failure([.. local]);
                }

                if (PurchaseOrderRules.CanEditLines(order.Status))
                {
                    order.SupplierId = input.SupplierId;
                    order.OrderDate = input.OrderDate;
                }

                order.ExpectedDate = input.ExpectedDate;
                order.Notes = notes;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Conflict,
            cancellationToken);

        return result.Succeeded
            ? Result.Success((await GetAsync(id, cancellationToken))!)
            : Result.Failure<PurchaseOrderDetail>([.. result.Errors]);
    }

    public async Task<Result<PurchaseOrderLineItem>> AddLineAsync(
        int purchaseOrderId, int productId, int quantity, long? unitCostCents = null, CancellationToken cancellationToken = default)
    {
        var errors = new List<Error>();
        ValidateQuantityAndCost(quantity, unitCostCents, PurchaseOrderFields.Quantity, PurchaseOrderFields.UnitCost, errors);
        if (errors.Count > 0)
        {
            return Result.Failure<PurchaseOrderLineItem>([.. errors]);
        }

        var result = await InTransactionAsync<Result<long>>(
            async () =>
            {
                var order = await db.PurchaseOrders.AsNoTracking().Where(o => o.Id == purchaseOrderId)
                    .Select(o => new { o.Status, o.Number, o.SupplierId }).FirstOrDefaultAsync(cancellationToken);
                if (order is null)
                {
                    return Result.Failure<long>(NotFound());
                }

                if (!PurchaseOrderRules.CanEditLines(order.Status))
                {
                    return Result.Failure<long>(LinesLocked(order.Number, order.Status));
                }

                var costs = await ResolveCostsAsync(order.SupplierId, [productId], cancellationToken);
                if (!costs.TryGetValue(productId, out var cost))
                {
                    return Result.Failure<long>("po.product_missing", "That part no longer exists.", PurchaseOrderFields.ProductId);
                }

                if (!cost.IsActive)
                {
                    return Result.Failure<long>(ProductArchived(cost.Sku, PurchaseOrderFields.ProductId));
                }

                if (await db.PurchaseOrderLines.AnyAsync(l => l.PurchaseOrderId == purchaseOrderId && l.ProductId == productId, cancellationToken))
                {
                    return Result.Failure<long>(LineDuplicate(cost.Sku));
                }

                var unitCost = unitCostCents ?? DefaultCost(cost.LinkCostCents, cost.ProductCostCents);
                if (unitCost > MaxUnitCostCents)
                {
                    return Result.Failure<long>(CostTooLarge(PurchaseOrderFields.UnitCost));
                }

                var line = new PurchaseOrderLine { PurchaseOrderId = purchaseOrderId, ProductId = productId, QuantityOrdered = quantity, UnitCostCents = unitCost };
                db.PurchaseOrderLines.Add(line);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success(line.Id);
            },
            ex => ex is DbUpdateException update && DbErrors.IsUniqueViolation(update, out _)
                ? Result.Failure<long>(LineDuplicate(null))
                : Conflict<long>(ex),
            cancellationToken);

        return result.Succeeded
            ? Result.Success((await GetLineAsync(result.Value, cancellationToken))!)
            : Result.Failure<PurchaseOrderLineItem>([.. result.Errors]);
    }

    public async Task<Result<PurchaseOrderLineItem>> UpdateLineAsync(
        int purchaseOrderId, long lineId, int quantity, long unitCostCents, CancellationToken cancellationToken = default)
    {
        var errors = new List<Error>();
        ValidateQuantityAndCost(quantity, unitCostCents, PurchaseOrderFields.Quantity, PurchaseOrderFields.UnitCost, errors);
        if (errors.Count > 0)
        {
            return Result.Failure<PurchaseOrderLineItem>([.. errors]);
        }

        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var line = await db.PurchaseOrderLines.Include(l => l.PurchaseOrder)
                    .FirstOrDefaultAsync(l => l.Id == lineId && l.PurchaseOrderId == purchaseOrderId, cancellationToken);
                if (line is null)
                {
                    return Result.Failure(LineMissing());
                }

                if (!PurchaseOrderRules.CanEditLines(line.PurchaseOrder!.Status))
                {
                    return Result.Failure(LinesLocked(line.PurchaseOrder.Number, line.PurchaseOrder.Status));
                }

                line.QuantityOrdered = quantity;
                line.UnitCostCents = unitCostCents;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Conflict,
            cancellationToken);

        return result.Succeeded
            ? Result.Success((await GetLineAsync(lineId, cancellationToken))!)
            : Result.Failure<PurchaseOrderLineItem>([.. result.Errors]);
    }

    public Task<Result> RemoveLineAsync(int purchaseOrderId, long lineId, CancellationToken cancellationToken = default)
        => InTransactionAsync<Result>(
            async () =>
            {
                var line = await db.PurchaseOrderLines.Include(l => l.PurchaseOrder)
                    .FirstOrDefaultAsync(l => l.Id == lineId && l.PurchaseOrderId == purchaseOrderId, cancellationToken);
                if (line is null)
                {
                    return Result.Success();
                }

                if (!PurchaseOrderRules.CanEditLines(line.PurchaseOrder!.Status))
                {
                    return Result.Failure(LinesLocked(line.PurchaseOrder.Number, line.PurchaseOrder.Status));
                }

                db.PurchaseOrderLines.Remove(line);
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Conflict,
            cancellationToken);

    public async Task<Result<IReadOnlyList<DraftChangeOutcome>>> ApplyDraftChangesAsync(
        IReadOnlyList<DraftLineChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0)
        {
            return Result.Success<IReadOnlyList<DraftChangeOutcome>>([]);
        }

        var errors = new List<Error>();
        for (var i = 0; i < changes.Count; i++)
        {
            if (changes[i].Kind == DraftChangeKind.SetQuantity)
            {
                ValidateQuantityAndCost(changes[i].Quantity, null, PurchaseOrderFields.Line(i, PurchaseOrderFields.Quantity), PurchaseOrderFields.UnitCost, errors);
            }
            else if (changes[i].Quantity > MaxQuantity)
            {
                errors.Add(new Error("po.quantity_too_large", $"Quantities cannot exceed {MaxQuantity:N0}.", PurchaseOrderFields.Line(i, PurchaseOrderFields.Quantity)));
            }
        }

        if (errors.Count > 0)
        {
            return Result.Failure<IReadOnlyList<DraftChangeOutcome>>([.. errors]);
        }

        return await InTransactionAsync<Result<IReadOnlyList<DraftChangeOutcome>>>(
            async () =>
            {
                var orderIds = changes.Select(c => c.PurchaseOrderId).Distinct().ToList();
                var orders = await db.PurchaseOrders.Include(o => o.Lines)
                    .Where(o => orderIds.Contains(o.Id))
                    .ToDictionaryAsync(o => o.Id, cancellationToken);

                // Validate everything before changing anything.
                var problems = new List<Error>();
                foreach (var id in orderIds)
                {
                    if (!orders.TryGetValue(id, out var order))
                    {
                        problems.Add(NotFound());
                    }
                    else if (!PurchaseOrderRules.CanEditLines(order.Status))
                    {
                        problems.Add(new Error("po.not_editable", $"{order.Number} is {Describe(order.Status)}. Lines can only be changed while the order is a draft.", PurchaseOrderFields.Lines));
                    }
                }

                if (problems.Count > 0)
                {
                    return Result.Failure<IReadOnlyList<DraftChangeOutcome>>([.. problems]);
                }

                foreach (var change in changes.Where(c => c.Kind == DraftChangeKind.SetQuantity))
                {
                    if (!orders[change.PurchaseOrderId].Lines.Any(l => l.ProductId == change.ProductId))
                    {
                        problems.Add(new Error("po.line_not_found", $"That part is not on {orders[change.PurchaseOrderId].Number}, so its quantity cannot be changed.", PurchaseOrderFields.Lines));
                    }
                }

                if (problems.Count > 0)
                {
                    return Result.Failure<IReadOnlyList<DraftChangeOutcome>>([.. problems]);
                }

                var costs = new Dictionary<int, Dictionary<int, ProductCosts>>();
                foreach (var order in orders.Values)
                {
                    var productIds = changes.Where(c => c.PurchaseOrderId == order.Id && c.Kind == DraftChangeKind.Add).Select(c => c.ProductId);
                    costs[order.Id] = await ResolveCostsAsync(order.SupplierId, productIds, cancellationToken);
                }

                var outcomes = new List<DraftChangeOutcome>(changes.Count);
                foreach (var change in changes)
                {
                    var order = orders[change.PurchaseOrderId];
                    var existing = order.Lines.FirstOrDefault(l => l.ProductId == change.ProductId);

                    if (change.Kind == DraftChangeKind.SetQuantity)
                    {
                        var previous = existing!.QuantityOrdered;
                        existing.QuantityOrdered = change.Quantity;
                        outcomes.Add(new DraftChangeOutcome(change, DraftChangeStatus.Updated, previous, null));
                        continue;
                    }

                    string? skip = null;
                    ProductCosts? cost = null;
                    if (change.Quantity < 1)
                    {
                        skip = "The quantity is below 1.";
                    }
                    else if (!costs[order.Id].TryGetValue(change.ProductId, out cost))
                    {
                        skip = "The part no longer exists.";
                    }
                    else if (!cost.IsActive)
                    {
                        skip = $"{cost.Sku} is archived. Restore it before ordering it.";
                    }
                    else if (existing is not null)
                    {
                        skip = $"{cost.Sku} is already on {order.Number}.";
                    }

                    if (skip is not null)
                    {
                        outcomes.Add(new DraftChangeOutcome(change, DraftChangeStatus.Skipped, null, skip));
                        continue;
                    }

                    var unitCost = DefaultCost(cost!.LinkCostCents, cost.ProductCostCents);
                    if (unitCost > MaxUnitCostCents)
                    {
                        outcomes.Add(new DraftChangeOutcome(change, DraftChangeStatus.Skipped, null, $"The cost of {cost.Sku} is too large to order."));
                        continue;
                    }

                    order.Lines.Add(new PurchaseOrderLine { ProductId = change.ProductId, QuantityOrdered = change.Quantity, UnitCostCents = unitCost });
                    outcomes.Add(new DraftChangeOutcome(change, DraftChangeStatus.Added, null, null));
                }

                await db.SaveChangesAsync(cancellationToken);
                return Result.Success<IReadOnlyList<DraftChangeOutcome>>(outcomes);
            },
            Conflict<IReadOnlyList<DraftChangeOutcome>>,
            cancellationToken);
    }

    // ---- transitions -------------------------------------------------------------------------------------------

    public async Task<Result<PurchaseOrderDetail>> SubmitAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var order = await db.PurchaseOrders.Include(o => o.Supplier).Include(o => o.Lines).ThenInclude(l => l.Product)
                    .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
                if (CheckLoaded(order, rowVersion) is { } problem)
                {
                    return problem;
                }

                if (!PurchaseOrderRules.CanSubmit(order!.Status))
                {
                    return Result.Failure(InvalidTransition(order, "submitted"));
                }

                if (order.Lines.Count == 0)
                {
                    return Result.Failure("po.no_lines", "Add at least one line before submitting.", PurchaseOrderFields.Lines);
                }

                var errors = new List<Error>();
                if (!order.Supplier!.IsActive)
                {
                    errors.Add(SupplierArchived());
                }

                foreach (var line in order.Lines.Where(l => !l.Product!.IsActive))
                {
                    errors.Add(ProductArchived(line.Product!.Sku, PurchaseOrderFields.Lines));
                }

                if (errors.Count > 0)
                {
                    return Result.Failure([.. errors]);
                }

                if (order.ExpectedDate is null)
                {
                    var productIds = order.Lines.Select(l => l.ProductId).ToList();
                    var leadTime = await db.ProductSuppliers.AsNoTracking()
                        .Where(l => l.SupplierId == order.SupplierId && productIds.Contains(l.ProductId) && l.LeadTimeDays != null)
                        .MaxAsync(l => l.LeadTimeDays, cancellationToken);
                    if (leadTime is { } days)
                    {
                        order.ExpectedDate = order.OrderDate.AddDays(days);
                    }
                }

                order.Status = PurchaseOrderStatus.Submitted;
                order.SubmittedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Conflict,
            cancellationToken);

        if (!result.Succeeded)
        {
            return Result.Failure<PurchaseOrderDetail>([.. result.Errors]);
        }

        var detail = (await GetAsync(id, cancellationToken))!;
        await events.PublishAsync(Event(PurchaseOrderEventType.Submitted, detail, actor, $"{detail.Lines.Count} line(s)"), cancellationToken);
        return Result.Success(detail);
    }

    public async Task<Result> CancelAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        string? number = null;
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var order = await db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
                if (CheckLoaded(order, rowVersion) is { } problem)
                {
                    return problem;
                }

                if (!PurchaseOrderRules.CanCancel(order!.Status))
                {
                    return Result.Failure(InvalidTransition(order, "cancelled", order.Status == PurchaseOrderStatus.PartiallyReceived ? " Close it short instead." : string.Empty));
                }

                number = order.Number;
                order.Status = PurchaseOrderStatus.Cancelled;
                order.ClosedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Conflict,
            cancellationToken);

        if (result.Succeeded)
        {
            await events.PublishAsync(Event(PurchaseOrderEventType.Cancelled, id, number!, actor, null), cancellationToken);
        }

        return result;
    }

    public async Task<Result> DeleteDraftAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        string? number = null;
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var order = await db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
                if (order is null)
                {
                    // Already gone (a double click): nothing left to delete.
                    return Result.Success();
                }

                if (rowVersion is { } expected && order.RowVersion != expected)
                {
                    return Result.Failure(ConflictError());
                }

                if (!PurchaseOrderRules.CanDelete(order.Status))
                {
                    return Result.Failure("po.delete_not_draft", $"{order.Number} is {Describe(order.Status)}. Only a draft can be deleted; cancel it instead.");
                }

                number = order.Number;
                db.PurchaseOrders.Remove(order); // The lines go with it (cascade); a draft has no receipts.
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Conflict,
            cancellationToken);

        if (result.Succeeded && number is not null)
        {
            await events.PublishAsync(Event(PurchaseOrderEventType.DraftDeleted, id, number, actor, null), cancellationToken);
        }

        return result;
    }

    public async Task<Result> CloseShortAsync(int id, PurchaseOrderActor actor, long? rowVersion = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        string? number = null;
        var result = await InTransactionAsync<Result>(
            async () =>
            {
                var order = await db.PurchaseOrders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
                if (CheckLoaded(order, rowVersion) is { } problem)
                {
                    return problem;
                }

                if (!PurchaseOrderRules.CanCloseShort(order!.Status))
                {
                    return Result.Failure(InvalidTransition(order, "closed short", " Only an order that is partly received can be closed short."));
                }

                number = order.Number;
                order.Status = PurchaseOrderStatus.Received;
                order.ClosedAt = clock.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return Result.Success();
            },
            Conflict,
            cancellationToken);

        if (result.Succeeded)
        {
            await events.PublishAsync(Event(PurchaseOrderEventType.ClosedShort, id, number!, actor, null), cancellationToken);
        }

        return result;
    }

    // ---- receiving ---------------------------------------------------------------------------------------------

    public async Task<Result<ReceiptResult>> ReceiveAsync(
        int purchaseOrderId,
        IReadOnlyList<ReceiveLineInput> lines,
        PurchaseOrderActor actor,
        string? reference = null,
        string? note = null,
        bool allowOverReceipt = false,
        long? rowVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        RequireActor(actor);

        var cleanReference = Clean(reference);
        var cleanNote = Clean(note);
        var errors = new List<Error>();
        if (cleanReference is { Length: > StockTransaction.ReferenceMaxLength })
        {
            errors.Add(new Error("po.reference_too_long", $"Reference can be at most {StockTransaction.ReferenceMaxLength} characters.", PurchaseOrderFields.Reference));
        }

        if (cleanNote is { Length: > StockTransaction.NoteMaxLength })
        {
            errors.Add(new Error("po.note_too_long", $"Note can be at most {StockTransaction.NoteMaxLength} characters.", PurchaseOrderFields.Note));
        }

        for (var i = 0; i < lines.Count; i++)
        {
            var field = PurchaseOrderFields.Line(i, PurchaseOrderFields.Quantity);
            if (lines[i].Quantity < 0)
            {
                errors.Add(new Error("po.quantity_negative", "Quantity received cannot be negative.", field));
            }
            else if (lines[i].Quantity > MaxQuantity)
            {
                errors.Add(new Error("po.quantity_too_large", $"Quantities cannot exceed {MaxQuantity:N0}.", field));
            }
        }

        if (lines.GroupBy(l => l.LineId).Any(g => g.Count() > 1))
        {
            errors.Add(new Error("po.line_duplicate", "A line can appear only once in a receipt.", PurchaseOrderFields.Lines));
        }

        if (errors.Count > 0)
        {
            return Result.Failure<ReceiptResult>([.. errors]);
        }

        if (lines.All(l => l.Quantity == 0))
        {
            return Result.Failure<ReceiptResult>("po.nothing_to_receive", "Enter a quantity for at least one line.", PurchaseOrderFields.Lines);
        }

        PurchaseOrderStatus status = default;
        string number = string.Empty;

        var result = await InTransactionAsync<Result<ReceiptResult>>(
            async () =>
            {
                var order = await db.PurchaseOrders.Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == purchaseOrderId, cancellationToken);
                if (CheckLoaded(order, rowVersion) is { } problem)
                {
                    return Result.Failure<ReceiptResult>([.. problem.Errors]);
                }

                number = order!.Number;
                if (!PurchaseOrderRules.CanReceive(order.Status))
                {
                    return Result.Failure<ReceiptResult>(
                        "po.not_receivable",
                        order.Status == PurchaseOrderStatus.Draft
                            ? $"{order.Number} is still a draft. Submit it before receiving goods."
                            : $"{order.Number} is {Describe(order.Status)}; nothing can be received against it.");
                }

                // Outstanding is judged here, on lines read inside the transaction, not on what the page showed.
                var byId = order.Lines.ToDictionary(l => l.Id);
                var planned = new List<(int InputIndex, PurchaseOrderLine Line, int Quantity)>();
                var problems = new List<Error>();
                for (var i = 0; i < lines.Count; i++)
                {
                    if (!byId.TryGetValue(lines[i].LineId, out var line))
                    {
                        problems.Add(LineMissing(PurchaseOrderFields.Line(i, PurchaseOrderFields.Quantity)));
                        continue;
                    }

                    if (lines[i].Quantity == 0)
                    {
                        continue;
                    }

                    var outstanding = Math.Max(0, line.QuantityOrdered - line.QuantityReceived);
                    if (lines[i].Quantity > outstanding)
                    {
                        if (!allowOverReceipt)
                        {
                            problems.Add(new Error(
                                "po.over_receipt",
                                outstanding == 0
                                    ? "Nothing is outstanding on this line."
                                    : $"Only {outstanding} outstanding on this line; {lines[i].Quantity} is more than was ordered.",
                                PurchaseOrderFields.Line(i, PurchaseOrderFields.Quantity)));
                            continue;
                        }

                        if (!actor.IsAdmin)
                        {
                            problems.Add(new Error(
                                "po.over_receipt_forbidden",
                                "Only an administrator can receive more than was ordered.",
                                PurchaseOrderFields.Line(i, PurchaseOrderFields.Quantity)));
                            continue;
                        }
                    }

                    planned.Add((i, line, lines[i].Quantity));
                }

                if (problems.Count > 0)
                {
                    return Result.Failure<ReceiptResult>([.. problems]);
                }

                var movements = planned
                    .Select(p => new StockMovement(
                        p.Line.ProductId,
                        StockTransactionType.Receive,
                        p.Quantity,
                        Reason: $"Received on {order.Number}",
                        Reference: cleanReference ?? order.Number,
                        Note: cleanNote,
                        UnitCostCents: p.Line.UnitCostCents,
                        PurchaseOrderLineId: p.Line.Id))
                    .ToList();

                // Joins this transaction, so a refusal (archived product, too large) leaves nothing behind.
                var posted = await stock.PostAsync(movements, new StockContext(actor.UserId), cancellationToken);
                if (!posted.Succeeded)
                {
                    return Result.Failure<ReceiptResult>([.. posted.Errors.Select(e => MapStockError(e, planned.Select(p => p.InputIndex).ToList()))]);
                }

                foreach (var (_, line, quantity) in planned)
                {
                    line.QuantityReceived += quantity;
                }

                order.Status = PurchaseOrderRules.StatusFor(order.Lines.Select(l => (l.QuantityOrdered, l.QuantityReceived)));
                order.ClosedAt = order.Status == PurchaseOrderStatus.Received ? clock.UtcNow : null;
                await db.SaveChangesAsync(cancellationToken);

                status = order.Status;
                return Result.Success(new ReceiptResult(order.Status, posted.Value, planned.Count, planned.Sum(p => p.Quantity)));
            },
            Conflict<ReceiptResult>,
            cancellationToken);

        if (result.Succeeded)
        {
            await events.PublishAsync(
                Event(PurchaseOrderEventType.Received, purchaseOrderId, number, actor, $"{result.Value.LinesReceived} line(s), {result.Value.UnitsReceived} unit(s); now {status}"),
                cancellationToken);
        }

        return result;
    }

    public async Task<Result<PostedMovement>> ReverseReceiptAsync(
        long transactionId, PurchaseOrderActor actor, string? note = null, CancellationToken cancellationToken = default)
    {
        RequireActor(actor);
        var cleanNote = Clean(note);
        if (cleanNote is { Length: > StockTransaction.NoteMaxLength })
        {
            return Result.Failure<PostedMovement>("po.note_too_long", $"Note can be at most {StockTransaction.NoteMaxLength} characters.", PurchaseOrderFields.Note);
        }

        int orderId = 0;
        string number = string.Empty;
        PurchaseOrderStatus status = default;

        var result = await InTransactionAsync<Result<PostedMovement>>(
            async () =>
            {
                var receipt = await db.StockTransactions.AsNoTracking()
                    .Where(t => t.Id == transactionId)
                    .Select(t => new { t.Type, t.QuantityDelta, t.PurchaseOrderLineId })
                    .FirstOrDefaultAsync(cancellationToken);
                if (receipt is null)
                {
                    return Result.Failure<PostedMovement>("po.receipt_missing", "That receipt does not exist.");
                }

                if (receipt.PurchaseOrderLineId is not { } lineId || receipt.Type != StockTransactionType.Receive)
                {
                    return Result.Failure<PostedMovement>("po.not_a_receipt", "That transaction is not a purchase order receipt.");
                }

                var order = await db.PurchaseOrders.Include(o => o.Lines).FirstAsync(o => o.Lines.Any(l => l.Id == lineId), cancellationToken);
                if (!PurchaseOrderRules.CanReceive(order.Status) && order.Status != PurchaseOrderStatus.Received)
                {
                    return Result.Failure<PostedMovement>("po.not_receivable", $"{order.Number} is {Describe(order.Status)}; its receipts cannot be reversed.");
                }

                var line = order.Lines.First(l => l.Id == lineId);
                var reversed = await stock.ReverseOrderReceiptAsync(transactionId, new StockContext(actor.UserId), cleanNote, cancellationToken);
                if (!reversed.Succeeded)
                {
                    return reversed;
                }

                line.QuantityReceived = Math.Max(0, line.QuantityReceived - receipt.QuantityDelta);
                order.Status = PurchaseOrderRules.StatusFor(order.Lines.Select(l => (l.QuantityOrdered, l.QuantityReceived)));
                order.ClosedAt = order.Status == PurchaseOrderStatus.Received ? order.ClosedAt ?? clock.UtcNow : null;
                await db.SaveChangesAsync(cancellationToken);

                orderId = order.Id;
                number = order.Number;
                status = order.Status;
                return reversed;
            },
            Conflict<PostedMovement>,
            cancellationToken);

        if (result.Succeeded)
        {
            await events.PublishAsync(Event(PurchaseOrderEventType.ReceiptReversed, orderId, number, actor, $"transaction #{transactionId}; now {status}"), cancellationToken);
        }

        return result;
    }

    // ---- internals ---------------------------------------------------------------------------------------------

    private sealed record ProductCosts(string Sku, bool IsActive, long ProductCostCents, long? LinkCostCents);

    /// <summary>The cost sources for each product (products that do not exist are absent).</summary>
    private async Task<Dictionary<int, ProductCosts>> ResolveCostsAsync(int supplierId, IEnumerable<int> productIds, CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToList();
        var products = await db.Products.AsNoTracking()
            .Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Sku, p.IsActive, p.UnitCostCents })
            .ToListAsync(cancellationToken);
        var links = await db.ProductSuppliers.AsNoTracking()
            .Where(l => l.SupplierId == supplierId && ids.Contains(l.ProductId))
            .ToDictionaryAsync(l => l.ProductId, l => l.UnitCostCents, cancellationToken);

        return products.ToDictionary(
            p => p.Id,
            p => new ProductCosts(p.Sku, p.IsActive, p.UnitCostCents, links.TryGetValue(p.Id, out var cost) ? cost : null));
    }

    /// <summary>The supplier's price when it has one above zero (zero means "not entered"), else the product's own cost.</summary>
    private static long DefaultCost(long? linkCostCents, long productCostCents)
        => linkCostCents is > 0 ? linkCostCents.Value : productCostCents;

    private IQueryable<PurchaseOrderLineItem> LineItems(IQueryable<PurchaseOrderLine> lines, int supplierId)
        => lines.Select(l => new PurchaseOrderLineItem(
            l.Id,
            l.ProductId,
            l.Product!.Sku,
            l.Product.Name,
            l.Product.Unit,
            l.Product.IsActive,
            db.ProductSuppliers
                .Where(s => s.ProductId == l.ProductId && s.SupplierId == supplierId)
                .Select(s => s.SupplierSku)
                .FirstOrDefault(),
            l.QuantityOrdered,
            l.QuantityReceived,
            l.UnitCostCents));

    private async Task<PurchaseOrderLineItem?> GetLineAsync(long lineId, CancellationToken cancellationToken)
    {
        var supplierId = await db.PurchaseOrderLines.AsNoTracking().Where(l => l.Id == lineId).Select(l => (int?)l.PurchaseOrder!.SupplierId).FirstOrDefaultAsync(cancellationToken);
        if (supplierId is null)
        {
            return null;
        }

        return await LineItems(db.PurchaseOrderLines.AsNoTracking().Where(l => l.Id == lineId), supplierId.Value).FirstOrDefaultAsync(cancellationToken);
    }

    private static void ValidateQuantityAndCost(int quantity, long? unitCostCents, string quantityField, string costField, List<Error> errors)
    {
        if (quantity <= 0)
        {
            errors.Add(new Error("po.quantity_positive", "Quantity must be greater than zero.", quantityField));
        }
        else if (quantity > MaxQuantity)
        {
            errors.Add(new Error("po.quantity_too_large", $"Quantities cannot exceed {MaxQuantity:N0}.", quantityField));
        }

        if (unitCostCents is < 0)
        {
            errors.Add(new Error("po.cost_negative", "Unit cost cannot be negative.", costField));
        }
        else if (unitCostCents > MaxUnitCostCents)
        {
            errors.Add(CostTooLarge(costField));
        }
    }

    /// <summary>Common checks after loading an order for a transition: it exists and the page was not stale.</summary>
    private static Result? CheckLoaded(PurchaseOrder? order, long? rowVersion)
    {
        if (order is null)
        {
            return Result.Failure(NotFound());
        }

        return rowVersion is { } expected && order.RowVersion != expected ? Result.Failure(ConflictError()) : null;
    }

    /// <summary>Maps an error from the stock service (whose paths index the stock lines) onto the receipt form's fields.</summary>
    private static Error MapStockError(Error error, IReadOnlyList<int> inputIndexes)
    {
        if (error.Field is null || !error.Field.StartsWith($"{StockFields.Lines}[", StringComparison.Ordinal))
        {
            return error;
        }

        var close = error.Field.IndexOf(']', StringComparison.Ordinal);
        var tail = StockFields.Unindexed(error.Field);
        if (close < 0 || !int.TryParse(error.Field.AsSpan(StockFields.Lines.Length + 1, close - StockFields.Lines.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index >= inputIndexes.Count)
        {
            return error;
        }

        return tail switch
        {
            StockFields.Reference => error with { Field = PurchaseOrderFields.Reference },
            StockFields.Note => error with { Field = PurchaseOrderFields.Note },
            _ => error with { Field = PurchaseOrderFields.Line(inputIndexes[index], PurchaseOrderFields.Quantity) },
        };
    }

    /// <summary>
    /// Runs <paramref name="work"/> in one database transaction (joining one that is already open, which is then the caller's to
    /// commit) and commits when it succeeds. A lost optimistic-concurrency race becomes <paramref name="onConflict"/>.
    /// </summary>
    private async Task<TResult> InTransactionAsync<TResult>(Func<Task<TResult>> work, Func<DbUpdateException, TResult> onConflict, CancellationToken cancellationToken)
        where TResult : Result
    {
        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            var result = await work();
            if (own is not null && result.Succeeded)
            {
                await own.CommitAsync(cancellationToken);
            }

            return result;
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || DbErrors.IsUniqueViolation(ex, out _))
        {
            db.ChangeTracker.Clear();
            return onConflict(ex);
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync(); // An uncommitted transaction rolls back.
            }
        }
    }

    private static Result Conflict(DbUpdateException _) => Result.Failure(ConflictError());

    private static Result<T> Conflict<T>(DbUpdateException _) => Result.Failure<T>(ConflictError());

    private DateOnly Today() => DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());

    private static string? Clean(string? text)
    {
        var trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static void RequireActor(PurchaseOrderActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("A purchase order action needs the id of the user who made it.", nameof(actor));
        }
    }

    private PurchaseOrderEvent Event(PurchaseOrderEventType type, PurchaseOrderDetail order, PurchaseOrderActor actor, string? detail)
        => Event(type, order.Id, order.Number, actor, detail);

    private PurchaseOrderEvent Event(PurchaseOrderEventType type, int id, string number, PurchaseOrderActor actor, string? detail)
        => new(type, id, number, actor.UserId, detail, clock.UtcNow);

    private static string Describe(PurchaseOrderStatus status) => status switch
    {
        PurchaseOrderStatus.PartiallyReceived => "partly received",
        _ => status.ToString().ToLowerInvariant(),
    };

    // ---- errors ------------------------------------------------------------------------------------------------

    private static Error NotFound() => new("po.not_found", "That purchase order no longer exists.");

    private static Error ConflictError() => new("po.conflict", "Someone else changed this purchase order. Reload it and try again.");

    private static Error Closed(PurchaseOrder order) => new("po.closed", $"{order.Number} is {Describe(order.Status)} and can no longer be changed.");

    private static Error InvalidTransition(PurchaseOrder order, string verb, string hint = "")
        => new("po.invalid_transition", $"{order.Number} is {Describe(order.Status)}, so it cannot be {verb}.{hint}");

    private static Error LinesLocked(string number, PurchaseOrderStatus status)
        => new("po.lines_locked", $"{number} is {Describe(status)}. Lines can only be changed while the order is a draft.", PurchaseOrderFields.Lines);

    private static Error LineMissing(string? field = null) => new("po.line_missing", "That line is not on this order any more.", field);

    private static Error LineDuplicate(string? sku)
        => new("po.line_duplicate", sku is null ? "That part is already on this order." : $"{sku} is already on this order. Change its quantity instead.", PurchaseOrderFields.ProductId);

    private static Error SupplierArchived()
        => new("po.supplier_archived", "That supplier is archived. Restore it, or choose another supplier.", PurchaseOrderFields.SupplierId);

    private static Error ProductArchived(string sku, string field)
        => new("po.product_archived", $"{sku} is archived. Restore it before ordering it.", field);

    private static Error CostTooLarge(string field)
        => new("po.cost_too_large", $"Unit cost cannot exceed {MaxUnitCostCents / 100m:N2}.", field);
}
