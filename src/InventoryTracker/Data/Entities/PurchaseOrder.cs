namespace InventoryTracker.Data.Entities;

/// <summary>Where a purchase order is in its life. Stored as its name (text). Transitions are enforced by <c>PurchaseOrderService</c>.</summary>
public enum PurchaseOrderStatus
{
    /// <summary>Being written; lines are editable and it can still be deleted.</summary>
    Draft,

    /// <summary>Sent to the supplier; nothing received yet.</summary>
    Submitted,

    /// <summary>Some, but not all, of the ordered quantity has arrived.</summary>
    PartiallyReceived,

    /// <summary>Everything arrived, or the rest was written off with "close short". Terminal.</summary>
    Received,

    /// <summary>Will not be delivered. Terminal.</summary>
    Cancelled,
}

/// <summary>
/// An order placed with a <see cref="Supplier"/>. Drafts are the only deletable orders; everything later is kept for
/// the record. Numbers (<c>PO-2026-0001</c>) are issued from <see cref="DocumentSequence"/> and never reused.
/// </summary>
public class PurchaseOrder : IHasTimestamps, IHasRowVersion
{
    public const int NumberMaxLength = 30;
    public const int NotesMaxLength = 2000;

    public int Id { get; set; }

    /// <summary>Unique, e.g. <c>PO-2026-0001</c>.</summary>
    public required string Number { get; set; }

    public int SupplierId { get; set; }

    public Supplier? Supplier { get; set; }

    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    /// <summary>The business date of the order (a calendar date, no time zone).</summary>
    public DateOnly OrderDate { get; set; }

    /// <summary>When the goods are expected; optional.</summary>
    public DateOnly? ExpectedDate { get; set; }

    /// <summary>When it left Draft (UTC).</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>When it became Received or Cancelled (UTC).</summary>
    public DateTime? ClosedAt { get; set; }

    public string? Notes { get; set; }

    public required string CreatedById { get; set; }

    public ApplicationUser? CreatedBy { get; set; }

    public long RowVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<PurchaseOrderLine> Lines { get; set; } = [];
}
