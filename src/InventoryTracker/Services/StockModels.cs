using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

/// <summary>
/// One requested stock change. <see cref="Quantity"/> is what the user typed: a positive number for
/// <see cref="StockTransactionType.Receive"/> / <see cref="StockTransactionType.Issue"/> / <see cref="StockTransactionType.Return"/>,
/// a signed non-zero number for <see cref="StockTransactionType.Adjust"/>, and the counted on-hand total (zero or more) for
/// <see cref="StockTransactionType.Count"/>. The service derives the stored sign/delta. Reversals go through
/// <see cref="IStockService.ReverseAsync"/>, not here.
/// </summary>
/// <param name="OccurredAt">When it happened; <c>null</c> = now. Backdating is allowed, the future is not.</param>
public sealed record StockMovement(
    int ProductId,
    StockTransactionType Type,
    int Quantity,
    string? Reason = null,
    string? Reference = null,
    string? Note = null,
    DateTime? OccurredAt = null,
    long? UnitCostCents = null,
    long? PurchaseOrderLineId = null,
    long? ImportBatchId = null);

/// <summary>Who is posting. Passed explicitly (not read from the request) so background work and tests can post too.</summary>
public sealed record StockContext(string UserId);

/// <summary>What one input line turned into. <see cref="TransactionId"/> is <c>null</c> when a count matched the system (nothing posted).</summary>
public sealed record PostedMovement(int LineIndex, int ProductId, long? TransactionId, int QuantityDelta, int QuantityAfter);

/// <summary>One ledger row for the history and movements lists.</summary>
public sealed record StockMovementItem(
    long Id,
    int ProductId,
    string Sku,
    string ProductName,
    StockTransactionType Type,
    int QuantityDelta,
    int QuantityAfter,
    long? UnitCostCents,
    string? Reason,
    string? Reference,
    string? Note,
    long? PurchaseOrderLineId,
    long? ReversesTransactionId,
    long? ReversedByTransactionId,
    string UserName,
    DateTime OccurredAt,
    DateTime CreatedAt)
{
    /// <summary>True for a row that a later Reversal has already cancelled.</summary>
    public bool IsReversed => ReversedByTransactionId is not null;

    /// <summary>Whether the Reverse action applies: not itself a reversal and not already reversed.</summary>
    public bool CanReverse => Type != StockTransactionType.Reversal && !IsReversed;
}

/// <summary>
/// Global ledger filter. Sort is always newest posting first. <see cref="From"/> is inclusive and <see cref="To"/> exclusive,
/// both UTC instants on <c>OccurredAt</c> (pages convert from the user's local dates). <c>Search</c> matches SKU, product name, reason and reference.
/// </summary>
public class MovementQuery : ListQuery
{
    public int? ProductId { get; set; }

    public StockTransactionType? Type { get; set; }

    public string? UserId { get; set; }

    public DateTime? From { get; set; }

    public DateTime? To { get; set; }
}

/// <summary>A user who appears in the ledger.</summary>
public sealed record MovementUser(string Id, string Name);

/// <summary>A counted quantity for one product (stock count page).</summary>
public sealed record CountEntry(int ProductId, int Counted);

/// <summary>What a count would change, shown before anything is posted.</summary>
public sealed record CountPreviewLine(int ProductId, string Sku, string Name, string Unit, int OnHand, int Counted)
{
    public int Difference => Counted - OnHand;
}

/// <summary>A product whose cached on-hand disagrees with the sum of its ledger.</summary>
public sealed record StockMismatch(int ProductId, string Sku, string Name, int Cached, int Ledger);

/// <summary>Outcome of the consistency check. When <see cref="Fixed"/>, the cache of every mismatch was rewritten from the ledger.</summary>
public sealed record StockVerifyReport(int ProductsChecked, IReadOnlyList<StockMismatch> Mismatches, bool Fixed)
{
    public bool IsConsistent => Mismatches.Count == 0;
}

/// <summary>Form field names used in <see cref="Error.Field"/>. Line errors use the model-binding path, e.g. <c>Lines[2].Quantity</c>.</summary>
public static class StockFields
{
    public const string Lines = nameof(Lines);
    public const string Product = nameof(Product);
    public const string Type = nameof(Type);
    public const string Quantity = nameof(Quantity);
    public const string Reason = nameof(Reason);
    public const string Reference = nameof(Reference);
    public const string Note = nameof(Note);
    public const string OccurredAt = nameof(OccurredAt);
    public const string UnitCost = nameof(UnitCost);

    /// <summary>The field path for input line <paramref name="index"/> (0-based).</summary>
    public static string Line(int index, string field) => $"{Lines}[{index}].{field}";

    /// <summary>Strips a <c>Lines[n].</c> prefix, so a single-line form can attach the error to its plain field name.</summary>
    public static string? Unindexed(string? field)
    {
        if (field is null || !field.StartsWith($"{Lines}[", StringComparison.Ordinal))
        {
            return field;
        }

        var dot = field.IndexOf("].", StringComparison.Ordinal);
        return dot < 0 ? field : field[(dot + 2)..];
    }
}
