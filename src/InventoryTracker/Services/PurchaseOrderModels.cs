using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

/// <summary>The status machine of a purchase order, in one place (F05 FR-3).</summary>
public static class PurchaseOrderRules
{
    private static readonly Dictionary<PurchaseOrderStatus, PurchaseOrderStatus[]> Allowed = new()
    {
        [PurchaseOrderStatus.Draft] = [PurchaseOrderStatus.Submitted, PurchaseOrderStatus.Cancelled],
        [PurchaseOrderStatus.Submitted] = [PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Received, PurchaseOrderStatus.Cancelled],

        // Received by closing short, or by receiving the rest; Submitted again when every receipt was reversed.
        [PurchaseOrderStatus.PartiallyReceived] = [PurchaseOrderStatus.Received, PurchaseOrderStatus.Submitted],

        // Only a reversed receipt reopens a received order; it never goes back to Draft or Cancelled.
        [PurchaseOrderStatus.Received] = [PurchaseOrderStatus.PartiallyReceived, PurchaseOrderStatus.Submitted],
        [PurchaseOrderStatus.Cancelled] = [],
    };

    /// <summary>Whether an order may move from <paramref name="from"/> to <paramref name="to"/>. Staying put is not a transition.</summary>
    public static bool CanTransition(PurchaseOrderStatus from, PurchaseOrderStatus to)
        => Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>Submitted or partially received: goods are expected, and the quantities count as "on order".</summary>
    public static bool IsOpen(PurchaseOrderStatus status) => status is PurchaseOrderStatus.Submitted or PurchaseOrderStatus.PartiallyReceived;

    /// <summary>Goods can be received against it.</summary>
    public static bool CanReceive(PurchaseOrderStatus status) => IsOpen(status);

    /// <summary>Lines (and the supplier, order date) can be edited: drafts only.</summary>
    public static bool CanEditLines(PurchaseOrderStatus status) => status == PurchaseOrderStatus.Draft;

    /// <summary>Notes and expected date can be edited until the order is closed.</summary>
    public static bool CanEditHeader(PurchaseOrderStatus status) => status is not (PurchaseOrderStatus.Received or PurchaseOrderStatus.Cancelled);

    public static bool CanSubmit(PurchaseOrderStatus status) => status == PurchaseOrderStatus.Draft;

    public static bool CanCancel(PurchaseOrderStatus status) => status is PurchaseOrderStatus.Draft or PurchaseOrderStatus.Submitted;

    public static bool CanCloseShort(PurchaseOrderStatus status) => status == PurchaseOrderStatus.PartiallyReceived;

    public static bool CanDelete(PurchaseOrderStatus status) => status == PurchaseOrderStatus.Draft;

    /// <summary>The status implied by the quantities received, for an order that has been submitted.</summary>
    public static PurchaseOrderStatus StatusFor(IEnumerable<(int Ordered, int Received)> lines)
    {
        var any = false;
        var all = true;
        var total = 0L;
        foreach (var (ordered, received) in lines)
        {
            any = true;
            total += received;
            if (received < ordered)
            {
                all = false;
            }
        }

        if (any && all)
        {
            return PurchaseOrderStatus.Received;
        }

        return total > 0 ? PurchaseOrderStatus.PartiallyReceived : PurchaseOrderStatus.Submitted;
    }
}

/// <summary>Which orders a list shows (the status tabs). <see cref="Open"/> is Submitted plus PartiallyReceived.</summary>
public enum PurchaseOrderTab
{
    All,
    Open,
    Draft,
    Received,
    Cancelled,
}

/// <summary>
/// Purchase order list query. Search matches the order number. <see cref="From"/>/<see cref="To"/> are inclusive order dates.
/// Sort keys: <c>number</c>, <c>supplier</c>, <c>status</c>, <c>date</c>, <c>expected</c>, <c>total</c>, <c>outstanding</c>;
/// the default is newest order date first.
/// </summary>
public class PurchaseOrderListQuery : ListQuery
{
    public PurchaseOrderTab Tab { get; set; } = PurchaseOrderTab.All;

    public int? SupplierId { get; set; }

    public DateOnly? From { get; set; }

    public DateOnly? To { get; set; }
}

/// <summary>One row of the PO list. <see cref="OutstandingCents"/> is the value still expected: zero unless the order is open.</summary>
public sealed record PurchaseOrderListItem(
    int Id,
    string Number,
    int SupplierId,
    string SupplierName,
    PurchaseOrderStatus Status,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    int LineCount,
    long TotalCents,
    long OutstandingCents);

/// <summary>One line of an order, with the product it names and (when linked) the supplier's own SKU for it.</summary>
public sealed record PurchaseOrderLineItem(
    long Id,
    int ProductId,
    string Sku,
    string ProductName,
    string Unit,
    bool ProductIsActive,
    string? SupplierSku,
    int QuantityOrdered,
    int QuantityReceived,
    long UnitCostCents)
{
    /// <summary>Still to arrive; never negative (an over-received line has nothing outstanding).</summary>
    public int Outstanding => Math.Max(0, QuantityOrdered - QuantityReceived);

    public long LineTotalCents => QuantityOrdered * UnitCostCents;

    public long OutstandingCents => Outstanding * UnitCostCents;
}

/// <summary>Everything the detail, editor, receive and print pages need.</summary>
public sealed record PurchaseOrderDetail(
    int Id,
    string Number,
    PurchaseOrderStatus Status,
    int SupplierId,
    string SupplierName,
    bool SupplierIsActive,
    string? SupplierContactName,
    string? SupplierEmail,
    string? SupplierPhone,
    string? SupplierAddress,
    DateOnly OrderDate,
    DateOnly? ExpectedDate,
    DateTime? SubmittedAt,
    DateTime? ClosedAt,
    string? Notes,
    string CreatedById,
    string CreatedByName,
    long RowVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<PurchaseOrderLineItem> Lines)
{
    public long TotalCents => Lines.Sum(l => l.LineTotalCents);

    /// <summary>Value still expected; zero unless the order is open (a closed-short remainder is not outstanding).</summary>
    public long OutstandingCents => PurchaseOrderRules.IsOpen(Status) ? Lines.Sum(l => l.OutstandingCents) : 0;

    public int TotalOrdered => Lines.Sum(l => l.QuantityOrdered);

    public int TotalReceived => Lines.Sum(l => l.QuantityReceived);

    public bool CanEditLines => PurchaseOrderRules.CanEditLines(Status);

    public bool CanEditHeader => PurchaseOrderRules.CanEditHeader(Status);

    public bool CanSubmit => PurchaseOrderRules.CanSubmit(Status) && Lines.Count > 0;

    public bool CanReceive => PurchaseOrderRules.CanReceive(Status);

    public bool CanCancel => PurchaseOrderRules.CanCancel(Status);

    public bool CanCloseShort => PurchaseOrderRules.CanCloseShort(Status);

    public bool CanDelete => PurchaseOrderRules.CanDelete(Status);
}

/// <summary>Who is acting. Over-receiving is an Admin power, so the service needs to know.</summary>
public sealed record PurchaseOrderActor(string UserId, bool IsAdmin = false);

/// <summary>A line to create with a draft. A <c>null</c> cost takes the supplier link's price, else the product's.</summary>
public sealed record PurchaseOrderLineInput(int ProductId, int Quantity, long? UnitCostCents = null);

/// <summary>
/// The editable header. While the order is a draft everything applies (the supplier only while there are no lines);
/// once submitted only <see cref="ExpectedDate"/> and <see cref="Notes"/> may differ from what is stored.
/// </summary>
public sealed record PurchaseOrderHeaderInput(int SupplierId, DateOnly OrderDate, DateOnly? ExpectedDate, string? Notes);

/// <summary>How many of a line arrived now. Zero (or blank on the form) means "nothing this time".</summary>
public sealed record ReceiveLineInput(long LineId, int Quantity);

/// <summary>What a receipt did.</summary>
public sealed record ReceiptResult(PurchaseOrderStatus Status, IReadOnlyList<PostedMovement> Movements, int LinesReceived, int UnitsReceived);

/// <summary>A stock receipt (or its reversal) posted against one of the order's lines.</summary>
public sealed record PurchaseOrderReceiptItem(
    long TransactionId,
    long LineId,
    int ProductId,
    string Sku,
    string ProductName,
    StockTransactionType Type,
    int QuantityDelta,
    long? UnitCostCents,
    string? Reference,
    string? Note,
    string UserName,
    DateTime OccurredAt,
    bool IsReversed,
    bool CanReverse);

/// <summary>An order line that is still expected, for the product page.</summary>
public sealed record OpenOrderLineItem(
    int PurchaseOrderId,
    string Number,
    int SupplierId,
    string SupplierName,
    PurchaseOrderStatus Status,
    DateOnly? ExpectedDate,
    int QuantityOrdered,
    int QuantityReceived)
{
    public int Outstanding => Math.Max(0, QuantityOrdered - QuantityReceived);
}

/// <summary>One Draft order that holds a product, and how many of it.</summary>
public sealed record InDraftEntry(int PurchaseOrderId, string Number, int Quantity);

/// <summary>A product's quantity across all Draft orders. Drafts are intentions: they are never "on order".</summary>
public sealed record InDraftQuantity(int Total, IReadOnlyList<InDraftEntry> Orders);

public enum DraftChangeKind
{
    /// <summary>Put the product on the draft as a new line (skipped when it is already there).</summary>
    Add,

    /// <summary>Set the quantity of the product's existing line on the draft.</summary>
    SetQuantity,
}

/// <summary>One change to the lines of a Draft order, applied by <see cref="IPurchaseOrderService.ApplyDraftChangesAsync"/>.</summary>
public sealed record DraftLineChange(int PurchaseOrderId, int ProductId, DraftChangeKind Kind, int Quantity);

public enum DraftChangeStatus
{
    Added,
    Updated,
    Skipped,
}

/// <summary>What happened to one <see cref="DraftLineChange"/>. <see cref="PreviousQuantity"/> is set for an update; <see cref="Reason"/> for a skip.</summary>
public sealed record DraftChangeOutcome(DraftLineChange Change, DraftChangeStatus Status, int? PreviousQuantity, string? Reason);

/// <summary>A product offered when adding a line: supplier-linked products first, with the price the line would get.</summary>
public sealed record PurchaseOrderProductChoice(
    int ProductId,
    string Sku,
    string Name,
    string Unit,
    bool IsLinked,
    string? SupplierSku,
    long DefaultUnitCostCents,
    int? MinOrderQuantity,
    bool AlreadyOnOrder);

/// <summary>Form field names used in <see cref="Error.Field"/>. Receive-line errors use <c>Lines[i].Quantity</c> (the input index).</summary>
public static class PurchaseOrderFields
{
    public const string SupplierId = nameof(SupplierId);
    public const string OrderDate = nameof(OrderDate);
    public const string ExpectedDate = nameof(ExpectedDate);
    public const string Notes = nameof(Notes);
    public const string ProductId = nameof(ProductId);
    public const string Quantity = nameof(Quantity);
    public const string UnitCost = nameof(UnitCost);
    public const string Reference = nameof(Reference);
    public const string Note = nameof(Note);
    public const string Lines = nameof(Lines);

    public static string Line(int index, string field) => $"{Lines}[{index}].{field}";
}

public enum PurchaseOrderEventType
{
    Created,
    Submitted,
    Cancelled,
    DraftDeleted,
    Received,
    ClosedShort,
    ReceiptReversed,
}

/// <summary>Something that happened to an order. Published only after the change is committed.</summary>
public sealed record PurchaseOrderEvent(
    PurchaseOrderEventType Type,
    int PurchaseOrderId,
    string Number,
    string? UserId,
    string? Detail,
    DateTime OccurredAtUtc);

/// <summary>
/// Hook for reacting to purchase order events. F08 (audit log, notifications) registers an implementation; this feature
/// writes no audit rows itself. Register with <c>services.AddScoped&lt;IPurchaseOrderEvents, MyHandler&gt;()</c>.
/// </summary>
public interface IPurchaseOrderEvents
{
    Task HandleAsync(PurchaseOrderEvent purchaseOrderEvent, CancellationToken cancellationToken = default);
}

/// <summary>Fans an event out to every registered <see cref="IPurchaseOrderEvents"/>. A failing handler is logged, never thrown.</summary>
public sealed class PurchaseOrderEventPublisher(IEnumerable<IPurchaseOrderEvents> handlers, ILogger<PurchaseOrderEventPublisher> logger)
{
    public async Task PublishAsync(PurchaseOrderEvent purchaseOrderEvent, CancellationToken cancellationToken = default)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(purchaseOrderEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A notification problem must not undo or block a purchase order change that is already saved.
                logger.LogError(ex, "Purchase order event handler {Handler} failed for {EventType}", handler.GetType().Name, purchaseOrderEvent.Type);
            }
        }
    }
}
