using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

// ---- dashboard (F06 FR-1, FR-2) ---------------------------------------------------------------------------------------

/// <summary>The headline numbers. Values use <c>Product.UnitCost × on hand</c> (no historical costing); open orders are Submitted or PartiallyReceived.</summary>
public sealed record DashboardTiles(
    int ActiveSkus,
    long StockValueCents,
    int LowStockCount,
    int OutOfStockCount,
    int OpenPurchaseOrders,
    long OpenPurchaseOrderValueCents,
    int OverduePurchaseOrders);

/// <summary>A stock movement for the "latest activity" list.</summary>
public sealed record DashboardMovement(
    long Id,
    int ProductId,
    string Sku,
    string ProductName,
    StockTransactionType Type,
    int QuantityDelta,
    string UserName,
    DateTime OccurredAt);

/// <summary>An open order expected soon.</summary>
public sealed record DashboardDueOrder(
    int Id,
    string Number,
    int SupplierId,
    string SupplierName,
    PurchaseOrderStatus Status,
    DateOnly ExpectedDate,
    long OutstandingCents);

public sealed record DashboardSummary(
    DashboardTiles Tiles,
    IReadOnlyList<LowStockRow> LowStock,
    IReadOnlyList<DashboardMovement> RecentMovements,
    IReadOnlyList<DashboardDueOrder> DueThisWeek);

// ---- reorder suggestions (F06 FR-4 to FR-6) ---------------------------------------------------------------------------

/// <summary>
/// Low-stock page filter. <see cref="ListQuery.Search"/> matches SKU and name; <see cref="SupplierId"/> is the <em>preferred</em> supplier.
/// Sort keys: <c>urgency</c> (default: furthest below the reorder point first), <c>sku</c>, <c>name</c>, <c>category</c>, <c>onhand</c>,
/// <c>onorder</c>, <c>reorderpoint</c>, <c>supplier</c>.
/// </summary>
public class ReorderFilter : ListQuery
{
    public int? CategoryId { get; set; }

    public int? SupplierId { get; set; }
}

/// <summary>One low-stock product with what to order. <see cref="InDraft"/> is the quantity already sitting on Draft orders (not yet ordered).</summary>
public sealed record ReorderSuggestion(
    int ProductId,
    string Sku,
    string Name,
    string Unit,
    string? CategoryName,
    int OnHand,
    int OnOrder,
    int ReorderPoint,
    int ReorderQuantity,
    int SuggestedQuantity,
    int? SupplierId,
    string? SupplierName,
    bool SupplierIsActive,
    int? MinOrderQuantity,
    long? UnitCostCents,
    int InDraft,
    IReadOnlyList<InDraftEntry> Drafts)
{
    /// <summary>A draft order can be raised for it: it has a preferred supplier that is not archived.</summary>
    public bool CanOrder => SupplierId is not null && SupplierIsActive;
}

/// <summary>A quantity the user chose to order for a product.</summary>
public sealed record ReorderRequest(int ProductId, int Quantity);

public sealed record ReorderCreatedOrder(int PurchaseOrderId, string Number, int SupplierId, string SupplierName, int LineCount, long TotalCents);

/// <summary>An item that did not make it onto a draft order, and why.</summary>
public sealed record ReorderSkipped(int ProductId, string Sku, string Name, string Reason);

public sealed record ReorderDraftResult(IReadOnlyList<ReorderCreatedOrder> Created, IReadOnlyList<ReorderSkipped> Skipped);

/// <summary>The suggested-quantity rule (F06 FR-5), in one place.</summary>
public static class ReorderMath
{
    /// <summary>
    /// <c>max(ReorderQuantity, ReorderPoint − (OnHand + OnOrder))</c>, never below 1 (a product exactly at its reorder point with no
    /// reorder quantity still needs something), then raised to the supplier's minimum order quantity.
    /// </summary>
    public static int SuggestedQuantity(int onHand, int onOrder, int reorderPoint, int reorderQuantity, int? minOrderQuantity)
        => MinOrderRounding.RoundUp(Math.Max(1, Math.Max(reorderQuantity, reorderPoint - (onHand + onOrder))), minOrderQuantity);
}

// ---- reports (F06 FR-7) -----------------------------------------------------------------------------------------------

/// <summary>Valuation report filter. Sort keys: <c>name</c> (default), <c>sku</c>, <c>category</c>, <c>onhand</c>, <c>cost</c>, <c>value</c>.</summary>
public class ValuationFilter : ListQuery
{
    public int? CategoryId { get; set; }

    /// <summary>Archived parts still hold stock, but the report is of the live catalogue unless this is set.</summary>
    public bool IncludeArchived { get; set; }
}

public sealed record ValuationRow
{
    public int ProductId { get; init; }

    public string Sku { get; init; } = "";

    public string Name { get; init; } = "";

    public string? CategoryName { get; init; }

    public string Unit { get; init; } = "";

    public int OnHand { get; init; }

    public long UnitCostCents { get; init; }

    public long ValueCents { get; init; }

    public bool IsActive { get; init; }
}

public sealed record ValuationTotals(int Products, long Units, long ValueCents);

public sealed record CategoryValue
{
    /// <summary><c>null</c> for parts without a category.</summary>
    public string? CategoryName { get; init; }

    public int Products { get; init; }

    public long Units { get; init; }

    public long ValueCents { get; init; }
}

public sealed record ValuationReport(PagedResult<ValuationRow> Page, ValuationTotals Totals, IReadOnlyList<CategoryValue> ByCategory);

/// <summary>
/// Movements report filter. <see cref="FromUtc"/> is inclusive and <see cref="ToUtcExclusive"/> exclusive (pages convert the user's local days).
/// <see cref="ListQuery.Search"/> matches SKU, part name, reason and reference. Always newest first.
/// </summary>
public class MovementReportFilter : ListQuery
{
    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtcExclusive { get; set; }

    public StockTransactionType? Type { get; set; }

    public int? CategoryId { get; set; }

    public int? ProductId { get; set; }

    public string? UserId { get; set; }
}

public sealed record MovementReportRow
{
    public long Id { get; init; }

    public DateTime OccurredAt { get; init; }

    public int ProductId { get; init; }

    public string Sku { get; init; } = "";

    public string ProductName { get; init; } = "";

    public string? CategoryName { get; init; }

    public StockTransactionType Type { get; init; }

    public int QuantityDelta { get; init; }

    public int QuantityAfter { get; init; }

    public long? UnitCostCents { get; init; }

    public string? Reason { get; init; }

    public string? Reference { get; init; }

    public string UserName { get; init; } = "";
}

/// <summary><see cref="Out"/> is the total taken out, as a positive number.</summary>
public sealed record MovementTotals(int Count, long In, long Out);

public sealed record MovementReport(PagedResult<MovementReportRow> Page, MovementTotals Totals);

/// <summary>
/// Usage report filter: units issued per part in the period (<c>Issue</c> movements only), biggest consumers first.
/// <see cref="ListQuery.Search"/> matches SKU and name. Sort keys: <c>units</c> (default, descending), <c>sku</c>, <c>name</c>, <c>category</c>, <c>issues</c>, <c>value</c>.
/// </summary>
public class UsageFilter : ListQuery
{
    public DateTime? FromUtc { get; set; }

    public DateTime? ToUtcExclusive { get; set; }

    public int? CategoryId { get; set; }
}

public sealed record UsageRow
{
    public int ProductId { get; init; }

    public string Sku { get; init; } = "";

    public string Name { get; init; } = "";

    public string? CategoryName { get; init; }

    public string Unit { get; init; } = "";

    public long UnitsUsed { get; init; }

    public int Issues { get; init; }

    /// <summary>Units used at the part's current unit cost.</summary>
    public long ValueCents { get; init; }
}

public sealed record UsageTotals(int Products, long Units, long ValueCents);

public sealed record UsageReport(PagedResult<UsageRow> Page, UsageTotals Totals);

/// <summary>
/// Purchase order summary filter, by order date (calendar dates, both ends inclusive), supplier and status.
/// Sort keys: <c>date</c> (default, newest first), <c>number</c>, <c>supplier</c>, <c>status</c>, <c>expected</c>, <c>total</c>, <c>outstanding</c>.
/// </summary>
public class PurchaseOrderReportFilter : ListQuery
{
    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }

    public int? SupplierId { get; set; }

    public PurchaseOrderStatus? Status { get; set; }
}

public sealed record PurchaseOrderReportRow
{
    public int Id { get; init; }

    public string Number { get; init; } = "";

    public int SupplierId { get; init; }

    public string SupplierName { get; init; } = "";

    public PurchaseOrderStatus Status { get; init; }

    public DateOnly OrderDate { get; init; }

    public DateOnly? ExpectedDate { get; init; }

    public int Lines { get; init; }

    public long TotalCents { get; init; }

    /// <summary>What is still to arrive, at order prices; 0 unless the order is open.</summary>
    public long OutstandingCents { get; init; }
}

public sealed record SupplierOrderSummary
{
    public int SupplierId { get; init; }

    public string SupplierName { get; init; } = "";

    public int Orders { get; init; }

    public long TotalCents { get; init; }

    public long OutstandingCents { get; init; }
}

public sealed record PurchaseOrderTotals(int Orders, long TotalCents, long OutstandingCents);

public sealed record PurchaseOrderReport(PagedResult<PurchaseOrderReportRow> Page, PurchaseOrderTotals Totals, IReadOnlyList<SupplierOrderSummary> BySupplier);

/// <summary>
/// Dead stock filter: active parts with stock that have had no movement for <see cref="Days"/> days (default 90).
/// <see cref="ListQuery.Search"/> matches SKU and name. Sort keys: <c>last</c> (default, longest idle first), <c>sku</c>, <c>name</c>, <c>category</c>, <c>onhand</c>, <c>value</c>.
/// </summary>
public class DeadStockFilter : ListQuery
{
    public const int DefaultDays = 90;
    public const int MaxDays = 3650;

    public int Days { get; set; } = DefaultDays;

    public int? CategoryId { get; set; }

    /// <summary>Days clamped to 1..<see cref="MaxDays"/>.</summary>
    public int SafeDays => Math.Clamp(Days, 1, MaxDays);
}

public sealed record DeadStockRow
{
    public int ProductId { get; init; }

    public string Sku { get; init; } = "";

    public string Name { get; init; } = "";

    public string? CategoryName { get; init; }

    public string Unit { get; init; } = "";

    public int OnHand { get; init; }

    public long UnitCostCents { get; init; }

    public long ValueCents { get; init; }

    /// <summary>When the part last moved; <c>null</c> if it never has.</summary>
    public DateTime? LastMovementAt { get; init; }
}

public sealed record DeadStockTotals(int Products, long Units, long ValueCents);

public sealed record DeadStockReport(PagedResult<DeadStockRow> Page, DeadStockTotals Totals);
