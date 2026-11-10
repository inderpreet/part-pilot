namespace InventoryTracker.Services;

/// <summary>How far an approved requisition has been ordered (F13). Derived from its line links, never stored.</summary>
public enum RequisitionOrderState
{
    NotOrdered,
    PartlyOrdered,
    Ordered,
}

/// <summary>
/// An approved line that has not been sent to a purchase order yet, with the figures that help avoid ordering twice
/// (the same on hand, on order and in draft the BOM page shows).
/// </summary>
public sealed record ToOrderLine(
    long RequisitionLineId,
    int RequisitionId,
    string RequisitionNumber,
    string RequestedByName,
    DateOnly? NeededBy,
    int ProductId,
    string Sku,
    string ProductName,
    string Unit,
    bool ProductIsActive,
    int QuantityApproved,
    int OnHand,
    int OnOrder,
    int InDraft,
    int? PreferredSupplierId,
    string? PreferredSupplierName,
    bool PreferredSupplierIsActive,
    int? MinOrderQuantity)
{
    /// <summary>Why the line cannot be ordered at all, or <c>null</c>.</summary>
    public string? Blocked => ProductIsActive ? null : $"{Sku} is archived. Restore it before ordering it.";

    /// <summary>A warning only (F13): the supplier's minimum is above the approved quantity.</summary>
    public bool BelowMinimum => MinOrderQuantity is { } minimum && QuantityApproved < minimum;

    /// <summary>The supplier the line is grouped under: the preferred one, unless it is missing or archived.</summary>
    public int? GroupSupplierId => PreferredSupplierIsActive ? PreferredSupplierId : null;
}

/// <summary>A draft order a group can be added to.</summary>
public sealed record DraftOrderChoice(int PurchaseOrderId, string Number, DateOnly OrderDate, int LineCount);

/// <summary>One supplier's lines (or, with no supplier, the lines that need one picked) and that supplier's drafts.</summary>
public sealed record ToOrderGroup(int? SupplierId, string? SupplierName, IReadOnlyList<ToOrderLine> Lines, IReadOnlyList<DraftOrderChoice> Drafts);

public sealed record SupplierChoice(int Id, string Name);

/// <summary>The to-order list. When opened for one draft PO, <see cref="ForOrder"/> is set and only its supplier's lines and the unassigned ones show.</summary>
public sealed record ToOrderView(IReadOnlyList<ToOrderGroup> Groups, IReadOnlyList<SupplierChoice> Suppliers, DraftOrderChoice? ForOrder, int? ForOrderSupplierId, string? ForOrderSupplierName);

/// <summary>Lines to send to one supplier: onto a new draft (<see cref="PurchaseOrderId"/> null) or onto an existing draft.</summary>
public sealed record ConversionGroup(int SupplierId, int? PurchaseOrderId, IReadOnlyList<long> RequisitionLineIds);

/// <summary>What a conversion did to one purchase order.</summary>
public sealed record ConversionOutcome(int PurchaseOrderId, string Number, bool Created, int LineCount);

/// <summary>The order a requisition line went on, for the requisition page.</summary>
public sealed record LineOrderInfo(int PurchaseOrderId, string Number, int QuantityOrdered);

/// <summary>A requisition feeding a purchase order line, for the PO page.</summary>
public sealed record OrderLineSource(long PurchaseOrderLineId, int RequisitionId, string RequisitionNumber, int QuantityOrdered);

/// <summary>Field names for conversion errors: one per requisition line and one per supplier group.</summary>
public static class RequisitionOrderFields
{
    public static string Line(long requisitionLineId) => $"Line{requisitionLineId}";

    public static string Group(int supplierId) => $"Group{supplierId}";
}
