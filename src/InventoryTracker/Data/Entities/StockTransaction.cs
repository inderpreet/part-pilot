namespace InventoryTracker.Data.Entities;

/// <summary>What kind of movement a <see cref="StockTransaction"/> records. Stored as its name (text).</summary>
public enum StockTransactionType
{
    /// <summary>Goods in (+): purchase-order receipts, samples, anything arriving.</summary>
    Receive,

    /// <summary>Goods out (-): used, sold, consumed.</summary>
    Issue,

    /// <summary>Signed correction with a required reason (damaged, lost, found).</summary>
    Adjust,

    /// <summary>A physical count: the delta is the difference between the counted and the recorded quantity.</summary>
    Count,

    /// <summary>Goods returned to stock (+).</summary>
    Return,

    /// <summary>Negates an earlier transaction; points at it through <see cref="StockTransaction.ReversesTransactionId"/>.</summary>
    Reversal,
}

/// <summary>
/// One immutable row of the stock ledger. <c>Product.QuantityOnHand</c> always equals the sum of
/// <see cref="QuantityDelta"/> for the product; only <c>StockService</c> writes both. Rows are never updated or deleted
/// (a database trigger enforces it): a mistake is corrected by a <see cref="StockTransactionType.Reversal"/>.
/// </summary>
public class StockTransaction : IHasCreatedAt
{
    public const int ReasonMaxLength = 100;
    public const int ReferenceMaxLength = 100;
    public const int NoteMaxLength = 500;

    public long Id { get; set; }

    public int ProductId { get; set; }

    public Product? Product { get; set; }

    public StockTransactionType Type { get; set; }

    /// <summary>Signed change to the on-hand quantity; never 0.</summary>
    public int QuantityDelta { get; set; }

    /// <summary>On-hand quantity right after this row was posted (posting order, not <see cref="OccurredAt"/> order).</summary>
    public int QuantityAfter { get; set; }

    /// <summary>Cost per unit in minor currency units, when known (e.g. the price on a receipt).</summary>
    public long? UnitCostCents { get; set; }

    public string? Reason { get; set; }

    public string? Reference { get; set; }

    public string? Note { get; set; }

    /// <summary>Set by PO receiving (F05): the order line this receipt (or its reversal) belongs to.</summary>
    public long? PurchaseOrderLineId { get; set; }

    public PurchaseOrderLine? PurchaseOrderLine { get; set; }

    /// <summary>Set by CSV import (F07). The column exists now; its foreign key is added by WI-07-1.</summary>
    public long? ImportBatchId { get; set; }

    /// <summary>For a <see cref="StockTransactionType.Reversal"/>: the transaction it negates. Unique, so each row is reversed at most once.</summary>
    public long? ReversesTransactionId { get; set; }

    public StockTransaction? ReversesTransaction { get; set; }

    /// <summary>The user who posted it.</summary>
    public required string UserId { get; set; }

    public ApplicationUser? User { get; set; }

    /// <summary>When the movement happened (UTC). May be in the past (backdating); never in the future.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>When the row was posted (UTC).</summary>
    public DateTime CreatedAt { get; set; }
}
