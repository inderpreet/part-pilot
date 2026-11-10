using InventoryTracker.Data;
using InventoryTracker.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace InventoryTracker.Services;

public interface IRequisitionOrderService
{
    /// <summary>
    /// Approved lines with no order, grouped by preferred supplier. With <paramref name="purchaseOrderId"/> only that draft's
    /// supplier and the lines with no usable supplier are listed; <c>null</c> when that order is missing or not a draft.
    /// </summary>
    Task<ToOrderView?> GetToOrderAsync(int? purchaseOrderId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends approved lines to purchase orders in one transaction, through <see cref="IPurchaseOrderService"/>. Every line is
    /// re-checked inside it; any problem fails the whole action and leaves nothing behind.
    /// </summary>
    Task<Result<IReadOnlyList<ConversionOutcome>>> ConvertAsync(IReadOnlyList<ConversionGroup> groups, RequisitionActor actor, CancellationToken cancellationToken = default);

    /// <summary>Which requisitions feed each line of a purchase order.</summary>
    Task<IReadOnlyList<OrderLineSource>> GetSourcesForOrderAsync(int purchaseOrderId, CancellationToken cancellationToken = default);
}

/// <summary>Turns approved requisition lines into purchase order lines (F13). Never touches stock.</summary>
public sealed class RequisitionOrderService(
    AppDbContext db,
    IPurchaseOrderService purchaseOrders,
    IClock clock,
    RequisitionEventPublisher events) : IRequisitionOrderService
{
    public async Task<ToOrderView?> GetToOrderAsync(int? purchaseOrderId = null, CancellationToken cancellationToken = default)
    {
        DraftOrderChoice? forOrder = null;
        int? forSupplierId = null;
        string? forSupplierName = null;
        if (purchaseOrderId is { } orderId)
        {
            var order = await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Id == orderId)
                .Select(o => new { o.Id, o.Number, o.OrderDate, o.Status, o.SupplierId, SupplierName = o.Supplier!.Name, Lines = o.Lines.Count })
                .FirstOrDefaultAsync(cancellationToken);
            if (order is null || order.Status != PurchaseOrderStatus.Draft)
            {
                return null;
            }

            forOrder = new DraftOrderChoice(order.Id, order.Number, order.OrderDate, order.Lines);
            forSupplierId = order.SupplierId;
            forSupplierName = order.SupplierName;
        }

        var rows = await (
            from l in db.RequisitionLines.AsNoTracking()
            where l.Decision != null && l.Decision.Decision == RequisitionDecision.Approved
                  && l.Order == null
                  && l.Requisition!.Status == RequisitionStatus.Approved
            let preferred = db.ProductSuppliers.FirstOrDefault(s => s.ProductId == l.ProductId && s.IsPreferred)
            orderby l.Requisition!.DecidedAt, l.RequisitionId, l.Id
            select new
            {
                l.Id,
                l.RequisitionId,
                l.Requisition!.Number,
                Requester = l.Requisition.RequestedBy!.DisplayName,
                l.Requisition.NeededBy,
                l.ProductId,
                l.Product!.Sku,
                l.Product.Name,
                l.Product.Unit,
                l.Product.IsActive,
                l.Decision!.QuantityApproved,
                l.Product.QuantityOnHand,
                PreferredSupplierId = preferred == null ? (int?)null : preferred.SupplierId,
                PreferredSupplierName = preferred == null ? null : preferred.Supplier!.Name,
                PreferredSupplierIsActive = preferred != null && preferred.Supplier!.IsActive,
                MinOrderQuantity = preferred == null ? null : (int?)preferred.MinOrderQuantity,
            }).ToListAsync(cancellationToken);

        var productIds = rows.Select(r => r.ProductId).Distinct().ToList();
        var onOrder = productIds.Count == 0
            ? []
            : await db.OnOrder().Where(o => productIds.Contains(o.ProductId)).ToDictionaryAsync(o => o.ProductId, o => o.Quantity, cancellationToken);
        var inDraft = await purchaseOrders.GetInDraftByProductAsync(productIds, cancellationToken);

        var lines = rows.Select(r => new ToOrderLine(
                r.Id, r.RequisitionId, r.Number, r.Requester, r.NeededBy, r.ProductId, r.Sku, r.Name, r.Unit, r.IsActive, r.QuantityApproved,
                r.QuantityOnHand, onOrder.GetValueOrDefault(r.ProductId), inDraft.TryGetValue(r.ProductId, out var d) ? d.Total : 0,
                r.PreferredSupplierId, r.PreferredSupplierName, r.PreferredSupplierIsActive, r.MinOrderQuantity))
            .Where(l => forSupplierId is null || l.GroupSupplierId is null || l.GroupSupplierId == forSupplierId)
            .ToList();

        var supplierIds = lines.Where(l => l.GroupSupplierId is not null).Select(l => l.GroupSupplierId!.Value).Distinct().ToList();
        var drafts = (await db.PurchaseOrders.AsNoTracking()
                .Where(o => o.Status == PurchaseOrderStatus.Draft && supplierIds.Contains(o.SupplierId))
                .OrderBy(o => o.Number)
                .Select(o => new { o.SupplierId, Choice = new DraftOrderChoice(o.Id, o.Number, o.OrderDate, o.Lines.Count) })
                .ToListAsync(cancellationToken))
            .ToLookup(o => o.SupplierId, o => o.Choice);

        var groups = lines.Where(l => l.GroupSupplierId is not null)
            .GroupBy(l => (Id: l.GroupSupplierId!.Value, Name: l.PreferredSupplierName!))
            .OrderBy(g => g.Key.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(g => new ToOrderGroup(g.Key.Id, g.Key.Name, [.. g], [.. drafts[g.Key.Id]]))
            .ToList();

        var unassigned = lines.Where(l => l.GroupSupplierId is null).ToList();
        if (unassigned.Count > 0)
        {
            groups.Add(new ToOrderGroup(null, null, unassigned, []));
        }

        var suppliers = await db.Suppliers.AsNoTracking()
            .Where(s => s.IsActive)
            .OrderBy(s => s.Name)
            .Select(s => new SupplierChoice(s.Id, s.Name))
            .ToListAsync(cancellationToken);

        return new ToOrderView(groups, suppliers, forOrder, forSupplierId, forSupplierName);
    }

    public async Task<IReadOnlyList<OrderLineSource>> GetSourcesForOrderAsync(int purchaseOrderId, CancellationToken cancellationToken = default)
        => await db.RequisitionLineOrders.AsNoTracking()
            .Where(o => o.PurchaseOrderLine!.PurchaseOrderId == purchaseOrderId)
            .OrderBy(o => o.RequisitionLine!.Requisition!.Number)
            .Select(o => new OrderLineSource(o.PurchaseOrderLineId, o.RequisitionLine!.RequisitionId, o.RequisitionLine.Requisition!.Number, o.QuantityOrdered))
            .ToListAsync(cancellationToken);

    public async Task<Result<IReadOnlyList<ConversionOutcome>>> ConvertAsync(
        IReadOnlyList<ConversionGroup> groups, RequisitionActor actor, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(actor);
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new ArgumentException("A conversion needs the id of the user who made it.", nameof(actor));
        }

        if (!actor.CanOrder)
        {
            return Fail(new Error("requisition.not_buyer", "Only Staff, Managers and administrators can order requisition lines."));
        }

        var allLineIds = groups.SelectMany(g => g.RequisitionLineIds).ToList();
        if (allLineIds.Count == 0)
        {
            return Fail(new Error("requisition.nothing_selected", "Select at least one line to order."));
        }

        if (allLineIds.Distinct().Count() != allLineIds.Count)
        {
            return Fail(new Error("requisition.line_twice", "A line can be sent to only one order."));
        }

        // Groups for the same supplier and target are one order: one new draft per supplier, one batch per existing draft.
        var merged = groups
            .GroupBy(g => (g.SupplierId, g.PurchaseOrderId))
            .Select(g => new ConversionGroup(g.Key.SupplierId, g.Key.PurchaseOrderId, [.. g.SelectMany(x => x.RequisitionLineIds)]))
            .ToList();

        var poActor = new PurchaseOrderActor(actor.UserId, actor.IsAdmin);
        var outcomes = new List<ConversionOutcome>();
        var requisitionsTouched = new Dictionary<int, string>();

        var own = db.Database.CurrentTransaction is null ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        try
        {
            // Re-check every line inside the transaction (BEGIN IMMEDIATE, so nobody else converts between check and write).
            var lines = await db.RequisitionLines.AsNoTracking()
                .Where(l => allLineIds.Contains(l.Id))
                .Select(l => new
                {
                    l.Id,
                    l.RequisitionId,
                    l.Requisition!.Number,
                    l.Requisition.Status,
                    Approved = l.Decision != null && l.Decision.Decision == RequisitionDecision.Approved,
                    Quantity = l.Decision == null ? 0 : l.Decision.QuantityApproved,
                    Ordered = l.Order != null,
                    l.ProductId,
                    l.Product!.Sku,
                    l.Product.IsActive,
                })
                .ToDictionaryAsync(l => l.Id, cancellationToken);

            var errors = new List<Error>();
            foreach (var id in allLineIds)
            {
                if (!lines.TryGetValue(id, out var line))
                {
                    errors.Add(new Error("requisition.line_missing", "A selected line no longer exists. Reload the list.", RequisitionOrderFields.Line(id)));
                }
                else if (line.Ordered)
                {
                    errors.Add(AlreadyOrdered(line.Sku, line.Number, id));
                }
                else if (!line.Approved || line.Status != RequisitionStatus.Approved)
                {
                    errors.Add(new Error("requisition.line_not_approved", $"{line.Sku} on {line.Number} is not an approved line waiting to be ordered.", RequisitionOrderFields.Line(id)));
                }
                else if (!line.IsActive)
                {
                    errors.Add(new Error("requisition.part_archived", $"{line.Sku} is archived. Restore it before ordering it.", RequisitionOrderFields.Line(id)));
                }
            }

            var supplierIds = merged.Select(g => g.SupplierId).Distinct().ToList();
            var suppliers = await db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, cancellationToken);
            var targetIds = merged.Where(g => g.PurchaseOrderId is not null).Select(g => g.PurchaseOrderId!.Value).ToList();
            var targets = await db.PurchaseOrders.AsNoTracking().Where(o => targetIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, cancellationToken);
            foreach (var group in merged)
            {
                if (!suppliers.TryGetValue(group.SupplierId, out var supplier))
                {
                    errors.Add(new Error("requisition.supplier_missing", "Choose a supplier for the selected lines.", RequisitionOrderFields.Group(group.SupplierId)));
                }
                else if (!supplier.IsActive)
                {
                    errors.Add(new Error("requisition.supplier_archived", $"{supplier.Name} is archived. Choose another supplier.", RequisitionOrderFields.Group(group.SupplierId)));
                }

                if (group.PurchaseOrderId is { } targetId)
                {
                    if (!targets.TryGetValue(targetId, out var target))
                    {
                        errors.Add(new Error("requisition.order_missing", "That purchase order no longer exists.", RequisitionOrderFields.Group(group.SupplierId)));
                    }
                    else if (target.Status != PurchaseOrderStatus.Draft)
                    {
                        errors.Add(new Error("requisition.order_not_draft", $"{target.Number} is no longer a draft. Choose another order or a new draft.", RequisitionOrderFields.Group(group.SupplierId)));
                    }
                    else if (target.SupplierId != group.SupplierId)
                    {
                        errors.Add(new Error("requisition.order_other_supplier", $"{target.Number} is for a different supplier.", RequisitionOrderFields.Group(group.SupplierId)));
                    }
                }
            }

            if (errors.Count > 0)
            {
                return Fail([.. errors]);
            }

            var now = clock.UtcNow;
            foreach (var group in merged)
            {
                int orderId;
                string number;
                var created = group.PurchaseOrderId is null;
                if (group.PurchaseOrderId is { } existingId)
                {
                    orderId = existingId;
                    number = targets[existingId].Number;
                }
                else
                {
                    var draft = await purchaseOrders.CreateDraftAsync(group.SupplierId, poActor, null, null, cancellationToken);
                    if (!draft.Succeeded)
                    {
                        return Fail([.. draft.Errors.Select(e => e with { Field = RequisitionOrderFields.Group(group.SupplierId) })]);
                    }

                    orderId = draft.Value.Id;
                    number = draft.Value.Number;
                }

                // A PO has one line per part: requisition lines for the same part add up, and a part already on the draft grows.
                var groupLines = group.RequisitionLineIds.Select(id => lines[id]).ToList();
                var byProduct = groupLines.GroupBy(l => l.ProductId).ToDictionary(g => g.Key, g => g.Sum(l => l.Quantity));
                var productIds = byProduct.Keys.ToList();
                var existing = await db.PurchaseOrderLines.AsNoTracking()
                    .Where(l => l.PurchaseOrderId == orderId && productIds.Contains(l.ProductId))
                    .ToDictionaryAsync(l => l.ProductId, l => l.QuantityOrdered, cancellationToken);

                var changes = byProduct.Select(p => existing.TryGetValue(p.Key, out var current)
                        ? new DraftLineChange(orderId, p.Key, DraftChangeKind.SetQuantity, current + p.Value)
                        : new DraftLineChange(orderId, p.Key, DraftChangeKind.Add, p.Value))
                    .ToList();
                var applied = await purchaseOrders.ApplyDraftChangesAsync(changes, cancellationToken);
                if (!applied.Succeeded)
                {
                    return Fail([.. applied.Errors.Select(e => e with { Field = RequisitionOrderFields.Group(group.SupplierId) })]);
                }

                var skipped = applied.Value.Where(o => o.Status == DraftChangeStatus.Skipped).ToList();
                if (skipped.Count > 0)
                {
                    return Fail([.. skipped.Select(s => new Error(
                        "requisition.order_skipped",
                        s.Reason ?? "A part could not be added to the order.",
                        RequisitionOrderFields.Line(groupLines.First(l => l.ProductId == s.Change.ProductId).Id)))]);
                }

                var poLines = await db.PurchaseOrderLines.AsNoTracking()
                    .Where(l => l.PurchaseOrderId == orderId && productIds.Contains(l.ProductId))
                    .ToDictionaryAsync(l => l.ProductId, l => l.Id, cancellationToken);
                db.RequisitionLineOrders.AddRange(groupLines.Select(l => new RequisitionLineOrder
                {
                    RequisitionLineId = l.Id,
                    PurchaseOrderLineId = poLines[l.ProductId],
                    QuantityOrdered = l.Quantity,
                    ConvertedById = actor.UserId,
                    ConvertedAt = now,
                }));
                await db.SaveChangesAsync(cancellationToken);

                foreach (var line in groupLines)
                {
                    requisitionsTouched[line.RequisitionId] = line.Number;
                }

                outcomes.Add(new ConversionOutcome(orderId, number, created, groupLines.Count));
            }

            if (own is not null)
            {
                await own.CommitAsync(cancellationToken);
            }
        }
        catch (DbUpdateException ex) when (ex is DbUpdateConcurrencyException || DbErrors.IsUniqueViolation(ex, out _))
        {
            db.ChangeTracker.Clear();
            return Fail(new Error("requisition.already_ordered", "Someone else ordered some of these lines at the same time. Reload the list and try again."));
        }
        finally
        {
            if (own is not null)
            {
                await own.DisposeAsync(); // An uncommitted transaction rolls back.
            }
        }

        foreach (var (id, number) in requisitionsTouched)
        {
            await events.PublishAsync(new RequisitionEvent(RequisitionEventType.Converted, id, number, actor.UserId, clock.UtcNow), cancellationToken);
        }

        return Result.Success<IReadOnlyList<ConversionOutcome>>(outcomes);
    }

    private static Result<IReadOnlyList<ConversionOutcome>> Fail(params Error[] errors) => Result.Failure<IReadOnlyList<ConversionOutcome>>(errors);

    private static Error AlreadyOrdered(string sku, string number, long lineId)
        => new("requisition.already_ordered", $"{sku} on {number} has already been ordered.", RequisitionOrderFields.Line(lineId));
}
