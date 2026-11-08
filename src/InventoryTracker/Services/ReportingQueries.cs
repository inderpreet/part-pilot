using InventoryTracker.Data;
using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

/// <summary>Quantity of one product that is on order from suppliers right now.</summary>
/// <remarks>
/// A class with settable properties, not a positional record: EF Core can only filter, sort and page a query
/// after its projection when the projection is a member initialiser.
/// </remarks>
public sealed class OnOrderRow
{
    public int ProductId { get; init; }

    public int Quantity { get; init; }
}

/// <summary>
/// A product at or below its reorder point once what is already on order is counted (F06 FR-4), with the figures a
/// suggestion needs. The suggested quantity is worked out by the caller (it rounds up to the minimum order quantity).
/// </summary>
public sealed class LowStockRow
{
    public int ProductId { get; init; }

    public string Sku { get; init; } = "";

    public string Name { get; init; } = "";

    public int? CategoryId { get; init; }

    public string? CategoryName { get; init; }

    public string Unit { get; init; } = "";

    public int OnHand { get; init; }

    public int OnOrder { get; init; }

    public int ReorderPoint { get; init; }

    public int ReorderQuantity { get; init; }

    /// <summary>The product's preferred supplier link, if it has one (it need not be an active supplier).</summary>
    public int? PreferredSupplierId { get; init; }

    public string? PreferredSupplierName { get; init; }

    /// <summary>False when the preferred supplier is archived: it cannot receive a new purchase order.</summary>
    public bool PreferredSupplierIsActive { get; init; }

    public int? PreferredMinOrderQuantity { get; init; }

    public long? PreferredUnitCostCents { get; init; }
}

/// <summary>
/// The read queries behind the dashboard, the low-stock page and the reports, defined once so that every screen, the
/// nav badge and the CSV exports agree. They are composable <see cref="IQueryable{T}"/>s: callers add filters, ordering
/// and paging before running them. No tracking is needed, so none of them tracks.
/// </summary>
public static class ReportingQueries
{
    /// <summary>The statuses in which an order is waiting for goods.</summary>
    public static readonly PurchaseOrderStatus[] OpenStatuses = [PurchaseOrderStatus.Submitted, PurchaseOrderStatus.PartiallyReceived];

    /// <summary>
    /// What is on order per product: the unreceived part of every line of a Submitted or PartiallyReceived order.
    /// Drafts are not on order yet, and a Received order closed short no longer expects the rest.
    /// </summary>
    public static IQueryable<OnOrderRow> OnOrder(this AppDbContext db)
        => db.PurchaseOrderLines
            .Where(l => (l.PurchaseOrder!.Status == PurchaseOrderStatus.Submitted
                         || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived)
                        && l.QuantityOrdered > l.QuantityReceived)
            .GroupBy(l => l.ProductId)
            .Select(g => new OnOrderRow { ProductId = g.Key, Quantity = g.Sum(l => l.QuantityOrdered - l.QuantityReceived) });

    /// <summary>
    /// Active products with a reorder point whose on hand plus on order is at or below it. Products without a reorder
    /// point (0) and archived ones are never low. Unordered: callers sort.
    /// </summary>
    public static IQueryable<LowStockRow> LowStock(this AppDbContext db)
        => from p in db.Products
           where p.IsActive && p.ReorderPoint > 0
           let onOrder = db.PurchaseOrderLines
               .Where(l => l.ProductId == p.Id
                           && (l.PurchaseOrder!.Status == PurchaseOrderStatus.Submitted
                               || l.PurchaseOrder.Status == PurchaseOrderStatus.PartiallyReceived)
                           && l.QuantityOrdered > l.QuantityReceived)
               .Sum(l => (int?)(l.QuantityOrdered - l.QuantityReceived)) ?? 0
           where p.QuantityOnHand + onOrder <= p.ReorderPoint
           let preferred = db.ProductSuppliers.FirstOrDefault(s => s.ProductId == p.Id && s.IsPreferred)
           select new LowStockRow
           {
               ProductId = p.Id,
               Sku = p.Sku,
               Name = p.Name,
               CategoryId = p.CategoryId,
               CategoryName = p.Category!.Name,
               Unit = p.Unit,
               OnHand = p.QuantityOnHand,
               OnOrder = onOrder,
               ReorderPoint = p.ReorderPoint,
               ReorderQuantity = p.ReorderQuantity,
               PreferredSupplierId = preferred == null ? null : preferred.SupplierId,
               PreferredSupplierName = preferred == null ? null : preferred.Supplier!.Name,
               PreferredSupplierIsActive = preferred != null && preferred.Supplier!.IsActive,
               PreferredMinOrderQuantity = preferred == null ? null : preferred.MinOrderQuantity,
               PreferredUnitCostCents = preferred == null ? null : preferred.UnitCostCents,
           };
}
