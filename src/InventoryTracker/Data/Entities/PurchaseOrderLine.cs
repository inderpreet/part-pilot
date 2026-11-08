namespace InventoryTracker.Data.Entities;

/// <summary>One product on a purchase order. At most one line per product per order.</summary>
public class PurchaseOrderLine
{
    /// <summary>Largest quantity on one line (keeps totals comfortably inside a <c>long</c> of cents).</summary>
    public const int MaxQuantity = 1_000_000_000;

    public long Id { get; set; }

    public int PurchaseOrderId { get; set; }

    public PurchaseOrder? PurchaseOrder { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Always greater than zero.</summary>
    public int QuantityOrdered { get; set; }

    /// <summary>
    /// Cached sum of this line's receipts (receipts minus reversals); changes only through <c>PurchaseOrderService</c>,
    /// together with the matching stock transactions.
    /// </summary>
    public int QuantityReceived { get; set; }

    /// <summary>Price per unit in minor currency units; copied to the stock transaction when received.</summary>
    public long UnitCostCents { get; set; }

    /// <summary>Stock transactions posted against this line (receipts and their reversals).</summary>
    public List<StockTransaction> Receipts { get; set; } = [];
}
