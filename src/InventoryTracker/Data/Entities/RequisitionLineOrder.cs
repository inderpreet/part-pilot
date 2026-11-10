namespace InventoryTracker.Data.Entities;

/// <summary>
/// Links an approved requisition line to the purchase order line that orders it (F13). At most one per requisition line, so a
/// line is never ordered twice; several requisition lines may share one PO line. Removing the PO line (or deleting its draft)
/// removes the link by cascade, which puts the requisition line back on the "to order" list.
/// </summary>
public class RequisitionLineOrder
{
    public long Id { get; set; }

    public long RequisitionLineId { get; set; }

    public RequisitionLine? RequisitionLine { get; set; }

    public long PurchaseOrderLineId { get; set; }

    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    /// <summary>The approved quantity sent at conversion. Changing the PO line afterwards does not change it.</summary>
    public int QuantityOrdered { get; set; }

    public required string ConvertedById { get; set; }

    public ApplicationUser? ConvertedBy { get; set; }

    public DateTime ConvertedAt { get; set; }
}
