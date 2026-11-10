namespace InventoryTracker.Data.Entities;

/// <summary>What was decided about one requisition line (F12). Stored as its name (text).</summary>
public enum RequisitionDecision
{
    Approved,
    Rejected,
}

/// <summary>
/// The final decision on one requisition line. A separate table, so adding it did not rebuild <c>RequisitionLines</c> (D25).
/// A decision is never edited; written only by <c>RequisitionService</c>.
/// </summary>
public class RequisitionLineDecision
{
    public const int ReasonMaxLength = 500;

    public long Id { get; set; }

    public long RequisitionLineId { get; set; }

    public RequisitionLine? RequisitionLine { get; set; }

    public RequisitionDecision Decision { get; set; }

    /// <summary>Between 1 and the requested quantity when approved; 0 when rejected (database check).</summary>
    public int QuantityApproved { get; set; }

    /// <summary>Why the line was rejected or reduced. Optional on a full approval.</summary>
    public string? Reason { get; set; }

    public required string DecidedById { get; set; }

    public ApplicationUser? DecidedBy { get; set; }

    public DateTime DecidedAt { get; set; }
}

/// <summary>Why, when and by whom a requisition was cancelled instead of being decided (F12 FR-7). At most one per requisition.</summary>
public class RequisitionCancellation
{
    public const int ReasonMaxLength = 500;

    public int Id { get; set; }

    public int RequisitionId { get; set; }

    public Requisition? Requisition { get; set; }

    public required string Reason { get; set; }

    public required string CancelledById { get; set; }

    public ApplicationUser? CancelledBy { get; set; }

    public DateTime CancelledAt { get; set; }
}
