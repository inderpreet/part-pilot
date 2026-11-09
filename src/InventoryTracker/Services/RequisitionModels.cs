using InventoryTracker.Data.Entities;

namespace InventoryTracker.Services;

/// <summary>
/// Who is acting on a requisition. <paramref name="CanSeeAll"/> widens reading to everyone's requisitions;
/// <paramref name="IsAdmin"/> also allows changing someone else's. Everyone else works only on their own.
/// </summary>
public sealed record RequisitionActor(string UserId, bool CanSeeAll = false, bool IsAdmin = false);

/// <summary>The ownership and status rules, in one place so the service and the pages agree.</summary>
public static class RequisitionRules
{
    public static bool CanSee(RequisitionActor actor, string requestedById)
        => actor.CanSeeAll || actor.IsAdmin || requestedById == actor.UserId;

    /// <summary>Only the requester or an Admin may edit, submit, withdraw or delete.</summary>
    public static bool CanChange(RequisitionActor actor, string requestedById)
        => actor.IsAdmin || requestedById == actor.UserId;

    public static bool CanEditContent(RequisitionStatus status) => status == RequisitionStatus.Draft;

    public static bool CanWithdraw(RequisitionStatus status) => status == RequisitionStatus.Submitted;
}

/// <summary>Which requisitions a list shows. Viewers only ever get <see cref="Mine"/>.</summary>
public enum RequisitionTab
{
    Mine,
    All,
}

public class RequisitionListQuery : ListQuery
{
    public RequisitionTab Tab { get; set; } = RequisitionTab.Mine;

    public RequisitionStatus? Status { get; set; }

    /// <summary>Only honoured on the <see cref="RequisitionTab.All"/> tab, for an actor who can see everyone's.</summary>
    public string? RequesterId { get; set; }

    /// <summary>First day created, local calendar date, as picked in the filter.</summary>
    public DateOnly? From { get; set; }

    /// <summary>Last day created, local calendar date, as picked in the filter.</summary>
    public DateOnly? To { get; set; }

    /// <summary>The browser's time-zone offset in minutes; filled by site.js.</summary>
    public int? Tz { get; set; }

    /// <summary>Inclusive start of the created-date filter. Pages convert the user's local days.</summary>
    public DateTime? FromUtc { get; set; }

    /// <summary>Exclusive end of the created-date filter. Pages convert the user's local days.</summary>
    public DateTime? ToUtcExclusive { get; set; }
}

public sealed record RequisitionListItem(
    int Id,
    string Number,
    RequisitionStatus Status,
    string RequestedById,
    string RequestedByName,
    DateOnly? NeededBy,
    int LineCount,
    DateTime CreatedAt,
    DateTime? SubmittedAt);

/// <summary>One line, with the availability the requester and approver look at. Reading these figures changes nothing.</summary>
public sealed record RequisitionLineItem(
    long Id,
    int ProductId,
    string Sku,
    string ProductName,
    string Unit,
    bool ProductIsActive,
    int QuantityRequested,
    string? Note,
    int OnHand,
    int OnOrder);

/// <summary>A requisition as one actor sees it; <see cref="CanChange"/> and the flags below it are worked out for that actor.</summary>
public sealed record RequisitionDetail(
    int Id,
    string Number,
    RequisitionStatus Status,
    string RequestedById,
    string RequestedByName,
    DateOnly? NeededBy,
    string? Reason,
    DateTime? SubmittedAt,
    string? SubmittedByName,
    DateTime? DecidedAt,
    long RowVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<RequisitionLineItem> Lines,
    bool CanChange)
{
    public bool CanEdit => CanChange && RequisitionRules.CanEditContent(Status);

    public bool CanSubmit => CanEdit;

    public bool CanDelete => CanEdit;

    public bool CanWithdraw => CanChange && RequisitionRules.CanWithdraw(Status);
}

/// <summary>Someone who has raised a requisition, for the requester filter.</summary>
public sealed record RequisitionRequester(string Id, string DisplayName);

/// <summary>A row of the add-line picker.</summary>
public sealed record RequisitionProductChoice(
    int ProductId,
    string Sku,
    string Name,
    string Unit,
    string? Barcode,
    int OnHand,
    int OnOrder,
    bool AlreadyOnRequisition);

public sealed record RequisitionHeaderInput(DateOnly? NeededBy, string? Reason);

public sealed record RequisitionLineInput(int ProductId, int Quantity, string? Note = null);

/// <summary>Field names used in <see cref="Error.Field"/> so pages can attach messages to inputs.</summary>
public static class RequisitionFields
{
    public const string NeededBy = nameof(NeededBy);
    public const string Reason = nameof(Reason);
    public const string Lines = nameof(Lines);
    public const string ProductId = nameof(ProductId);
    public const string Quantity = nameof(Quantity);
    public const string Note = nameof(Note);
}

public enum RequisitionEventType
{
    Created,
    Submitted,
    Withdrawn,
}

/// <summary>Something that happened to a requisition. Published only after the change is committed.</summary>
public sealed record RequisitionEvent(
    RequisitionEventType Type,
    int RequisitionId,
    string Number,
    string? UserId,
    DateTime OccurredAtUtc);

/// <summary>
/// Hook for reacting to requisition events. F08 (audit log) registers an implementation; this feature writes no audit rows
/// itself. Register with <c>services.AddScoped&lt;IRequisitionEvents, MyHandler&gt;()</c>.
/// </summary>
public interface IRequisitionEvents
{
    Task HandleAsync(RequisitionEvent requisitionEvent, CancellationToken cancellationToken = default);
}

/// <summary>Fans an event out to every registered <see cref="IRequisitionEvents"/>. A failing handler is logged, never thrown.</summary>
public sealed class RequisitionEventPublisher(IEnumerable<IRequisitionEvents> handlers, ILogger<RequisitionEventPublisher> logger)
{
    public async Task PublishAsync(RequisitionEvent requisitionEvent, CancellationToken cancellationToken = default)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(requisitionEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A notification problem must not undo or block a change that is already saved.
                logger.LogError(ex, "Requisition event handler {Handler} failed for {EventType}", handler.GetType().Name, requisitionEvent.Type);
            }
        }
    }
}
