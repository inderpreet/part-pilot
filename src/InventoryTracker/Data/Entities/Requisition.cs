namespace InventoryTracker.Data.Entities;

/// <summary>
/// Where a requisition is in its life. Stored as its name (text). Every status is in the check constraint from the first
/// migration so F12 and F13 do not have to rebuild the table (D25). Transitions are enforced by <c>RequisitionService</c>.
/// </summary>
public enum RequisitionStatus
{
    /// <summary>Being written; editable by the requester or an Admin, and it can still be deleted.</summary>
    Draft,

    /// <summary>Waiting for a decision. Locked until withdrawn.</summary>
    Submitted,

    /// <summary>A Manager approved at least part of it (F12).</summary>
    Approved,

    /// <summary>A Manager turned it down (F12). Terminal.</summary>
    Rejected,

    /// <summary>Called off after submission (F12, F13). Terminal.</summary>
    Cancelled,
}

/// <summary>
/// A request for catalog parts. Drafts are the only deletable requisitions; everything later is kept for the record.
/// Numbers (<c>REQ-2026-0001</c>) are issued from <see cref="DocumentSequence"/> and never reused. A requisition never
/// changes stock and does not count as on order.
/// </summary>
public class Requisition : IHasTimestamps, IHasRowVersion
{
    public const int NumberMaxLength = 30;
    public const int ReasonMaxLength = 1000;

    public int Id { get; set; }

    /// <summary>Unique, e.g. <c>REQ-2026-0001</c>.</summary>
    public required string Number { get; set; }

    public RequisitionStatus Status { get; set; } = RequisitionStatus.Draft;

    public required string RequestedById { get; set; }

    public ApplicationUser? RequestedBy { get; set; }

    /// <summary>When the parts are needed; optional (a calendar date, no time zone).</summary>
    public DateOnly? NeededBy { get; set; }

    /// <summary>Why the parts are needed. May be blank on a draft; required to submit.</summary>
    public string? Reason { get; set; }

    /// <summary>When it was last submitted (UTC); cleared when it is withdrawn.</summary>
    public DateTime? SubmittedAt { get; set; }

    /// <summary>Who submitted it (the requester, or an Admin on their behalf); cleared when it is withdrawn.</summary>
    public string? SubmittedById { get; set; }

    public ApplicationUser? SubmittedBy { get; set; }

    /// <summary>When it was approved or rejected (UTC), F12.</summary>
    public DateTime? DecidedAt { get; set; }

    public long RowVersion { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    public List<RequisitionLine> Lines { get; set; } = [];
}

/// <summary>One part on a requisition. At most one line per product per requisition.</summary>
public class RequisitionLine : IHasTimestamps
{
    public const int NoteMaxLength = 200;

    /// <summary>Largest quantity on one line, the same ceiling as a BOM line.</summary>
    public const int MaxQuantity = 1_000_000;

    public long Id { get; set; }

    public int RequisitionId { get; set; }

    public Requisition? Requisition { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    /// <summary>Whole units wanted; at least 1.</summary>
    public int QuantityRequested { get; set; }

    public string? Note { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}
