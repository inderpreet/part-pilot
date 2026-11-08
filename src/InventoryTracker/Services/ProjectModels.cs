namespace InventoryTracker.Services;

/// <summary>How a single BOM line stands against stock and orders (F10 FR-4). Worst first when comparing: Short, InDraft, Covered, Ready.</summary>
public enum LineState
{
    /// <summary>On hand covers what is still needed.</summary>
    Ready,

    /// <summary>On hand plus what is on submitted orders covers it.</summary>
    Covered,

    /// <summary>Only with the quantity sitting on Draft orders (which are not placed yet) is it covered.</summary>
    InDraft,

    /// <summary>Not covered, even counting drafts.</summary>
    Short,
}

/// <summary>The state of a whole project: its worst line, or Empty / Built.</summary>
public enum ProjectState
{
    Empty,
    Ready,
    Covered,
    InDraft,
    Short,
    Built,
}

/// <summary>Which projects a list shows.</summary>
public enum ProjectTab
{
    Active,
    Archived,
    All,
}

/// <summary>
/// The status math in one place, with no database (F10 FR-4). Needed = quantity per unit × remaining units; short = needed
/// − on hand; to order = short − on order (drafts are intentions and never reduce it).
/// </summary>
public static class BomStatusCalculator
{
    /// <summary>Units still to build: the target less everything built so far, never below zero.</summary>
    public static int RemainingUnits(int targetUnits, long builtUnits) => (int)Math.Max(0, targetUnits - builtUnits);

    public static LineCalc Line(int quantityPerUnit, int remainingUnits, int onHand, int onOrder, int inDraft)
    {
        var needed = (long)quantityPerUnit * remainingUnits;
        var onHandAtLeast0 = Math.Max(0, onHand);
        var shortBy = Math.Max(0, needed - onHandAtLeast0);
        var toOrder = Math.Max(0, shortBy - onOrder);

        var state = onHandAtLeast0 >= needed ? LineState.Ready
            : onHandAtLeast0 + (long)onOrder >= needed ? LineState.Covered
            : onHandAtLeast0 + (long)onOrder + inDraft >= needed ? LineState.InDraft
            : LineState.Short;

        return new LineCalc(needed, shortBy, toOrder, state);
    }

    /// <summary>Built when nothing remains, Empty with no lines, otherwise the worst line state.</summary>
    public static ProjectState Project(int lineCount, int remainingUnits, IEnumerable<LineState> states)
    {
        if (remainingUnits <= 0)
        {
            return ProjectState.Built;
        }

        if (lineCount == 0)
        {
            return ProjectState.Empty;
        }

        var worst = states.Aggregate(LineState.Ready, (a, b) => (LineState)Math.Max((int)a, (int)b));
        return worst switch
        {
            LineState.Short => ProjectState.Short,
            LineState.InDraft => ProjectState.InDraft,
            LineState.Covered => ProjectState.Covered,
            _ => ProjectState.Ready,
        };
    }
}

/// <summary>The numbers for one line, from <see cref="BomStatusCalculator.Line"/>.</summary>
public sealed record LineCalc(long Needed, long Short, long ToOrder, LineState State);

public class ProjectListQuery : ListQuery
{
    public ProjectTab Tab { get; set; } = ProjectTab.Active;
}

/// <summary>How many lines are in each state, for the list chips and the project header.</summary>
public sealed record ProjectStatusSummary(ProjectState State, int Ready, int Covered, int InDraft, int Short, int LineCount, int RemainingUnits)
{
    public static ProjectStatusSummary Empty { get; } = new(ProjectState.Empty, 0, 0, 0, 0, 0, 0);
}

/// <summary>One row of the project list. <see cref="BomCostCents"/> is the parts cost of one unit.</summary>
public sealed record ProjectListItem(
    int Id,
    string Name,
    string Number,
    int TargetUnits,
    int BuiltUnits,
    bool IsActive,
    int PartsCount,
    long BomCostCents,
    ProjectStatusSummary Status,
    DateTime UpdatedAt);

/// <summary>One part of a BOM, with the product it names.</summary>
public sealed record BomLineItem(
    long Id,
    int ProductId,
    string Sku,
    string ProductName,
    string Unit,
    bool ProductIsActive,
    int QuantityPerUnit,
    string? Note,
    long UnitCostCents)
{
    /// <summary>What this part costs in one unit of the project.</summary>
    public long LineCostCents => QuantityPerUnit * UnitCostCents;
}

/// <summary>Everything the project page needs apart from the status check.</summary>
public sealed record ProjectDetail(
    int Id,
    string Name,
    string Number,
    string? Description,
    int TargetUnits,
    string? Notes,
    bool IsActive,
    long RowVersion,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int BuiltUnits,
    int BuildCount,
    IReadOnlyList<BomLineItem> Lines)
{
    public int RemainingUnits => BomStatusCalculator.RemainingUnits(TargetUnits, BuiltUnits);

    /// <summary>Cost of the parts for one unit.</summary>
    public long BomCostCents => Lines.Sum(l => l.LineCostCents);

    /// <summary>A project that has been built is archived instead of deleted.</summary>
    public bool CanDelete => BuildCount == 0;
}

/// <summary>
/// The editable fields. <see cref="Number"/> is optional on create (blank = generate) and ignored on update: only
/// <see cref="IProjectService.ChangeNumberAsync"/> changes a number.
/// </summary>
public sealed record ProjectInput(string? Name, string? Number, string? Description, int TargetUnits, string? Notes);

public sealed record BomLineInput(int ProductId, int QuantityPerUnit, string? Note);

/// <summary>The short form for a part that is not in the catalog yet. Everything else takes the catalog defaults.</summary>
public sealed record NewPartInput(string? Sku, string? Name, string? Unit, long UnitCostCents);

/// <summary>A BOM line checked against stock and orders.</summary>
public sealed record BomLineStatus(
    long LineId,
    int ProductId,
    string Sku,
    string ProductName,
    string Unit,
    bool ProductIsActive,
    int QuantityPerUnit,
    string? Note,
    long UnitCostCents,
    long Needed,
    int OnHand,
    int OnOrder,
    int InDraft,
    IReadOnlyList<InDraftEntry> DraftOrders,
    long Short,
    long ToOrder,
    LineState State)
{
    /// <summary>What this part costs in one unit of the project.</summary>
    public long LineCostCents => QuantityPerUnit * UnitCostCents;

    /// <summary>The part of <see cref="ToOrder"/> that no Draft order holds yet.</summary>
    public long UncoveredByDrafts => Math.Max(0, ToOrder - InDraft);
}

/// <summary>The status check of one project (F10 FR-4).</summary>
public sealed record BomStatus(
    int ProjectId,
    int TargetUnits,
    int BuiltUnits,
    int RemainingUnits,
    ProjectState State,
    IReadOnlyList<BomLineStatus> Lines)
{
    /// <summary>Cost of the parts for one unit.</summary>
    public long CostPerUnitCents => Lines.Sum(l => l.LineCostCents);

    /// <summary>Cost of the parts the remaining units still need.</summary>
    public long RemainingCostCents => CostPerUnitCents * RemainingUnits;

    public ProjectStatusSummary Summary => new(
        State,
        Lines.Count(l => l.State == LineState.Ready),
        Lines.Count(l => l.State == LineState.Covered),
        Lines.Count(l => l.State == LineState.InDraft),
        Lines.Count(l => l.State == LineState.Short),
        Lines.Count,
        RemainingUnits);

    /// <summary>Lines that still need ordering (something to order).</summary>
    public IEnumerable<BomLineStatus> Shortages => Lines.Where(l => l.ToOrder > 0);
}

public enum ShortageAction
{
    /// <summary>Put the part on the target order.</summary>
    Add,

    /// <summary>Set the quantity of the part's line on the named draft.</summary>
    UpdateQuantity,

    /// <summary>Leave the part alone.</summary>
    Skip,
}

/// <summary>What the user decided for one short line. <see cref="PurchaseOrderId"/> names the draft to update.</summary>
public sealed record ShortageDecision(long BomLineId, ShortageAction Action, int Quantity, int? PurchaseOrderId = null);

/// <summary>Where added parts go: an existing Draft, or a new draft for a supplier.</summary>
public sealed record ShortageTarget(int? PurchaseOrderId, int? SupplierId)
{
    public static ShortageTarget ExistingDraft(int purchaseOrderId) => new(purchaseOrderId, null);

    public static ShortageTarget NewDraft(int supplierId) => new(null, supplierId);
}

public enum ShortageOutcomeKind
{
    Added,
    Updated,
    Skipped,
}

/// <summary>What happened to one decision. A skip carries its reason; an update the old quantity.</summary>
public sealed record ShortageOutcome(
    long BomLineId,
    string Sku,
    ShortageOutcomeKind Kind,
    int Quantity,
    int? PreviousQuantity,
    int? PurchaseOrderId,
    string? PurchaseOrderNumber,
    string? Reason);

/// <summary>The result of ordering shortages: one outcome per decision, and the draft that was created when there was one.</summary>
public sealed record AddShortagesResult(IReadOnlyList<ShortageOutcome> Outcomes, int? CreatedPurchaseOrderId, string? CreatedPurchaseOrderNumber)
{
    public IEnumerable<ShortageOutcome> Added => Outcomes.Where(o => o.Kind == ShortageOutcomeKind.Added);

    public IEnumerable<ShortageOutcome> Updated => Outcomes.Where(o => o.Kind == ShortageOutcomeKind.Updated);

    public IEnumerable<ShortageOutcome> Skipped => Outcomes.Where(o => o.Kind == ShortageOutcomeKind.Skipped);
}

/// <summary>A recorded build.</summary>
public sealed record ProjectBuildItem(long Id, int Units, string UserName, string? Note, DateTime CreatedAt);

/// <summary>What a build did: the parts issued (one movement per line) and the units still to build.</summary>
public sealed record BuildResult(long BuildId, int Units, IReadOnlyList<PostedMovement> Movements, int RemainingUnits);

/// <summary>Who is acting. Changing a project number is an Admin power, so the service needs to know.</summary>
public sealed record ProjectActor(string UserId, bool IsAdmin = false);

/// <summary>Form field names used in <see cref="Error.Field"/>.</summary>
public static class ProjectFields
{
    public const string Name = nameof(Name);
    public const string Number = nameof(Number);
    public const string Description = nameof(Description);
    public const string TargetUnits = nameof(TargetUnits);
    public const string Notes = nameof(Notes);
    public const string ProductId = nameof(ProductId);
    public const string QuantityPerUnit = nameof(QuantityPerUnit);
    public const string Note = nameof(Note);
    public const string Units = nameof(Units);
    public const string Sku = nameof(Sku);
    public const string Unit = nameof(Unit);
    public const string UnitCost = nameof(UnitCost);
    public const string Target = nameof(Target);
    public const string Lines = nameof(Lines);
}

public enum ProjectEventType
{
    Created,
    BomChanged,
    Built,
    NumberChanged,
}

/// <summary>Something that happened to a project. Published only after the change is committed.</summary>
public sealed record ProjectEvent(
    ProjectEventType Type,
    int ProjectId,
    string Number,
    string? UserId,
    string? Detail,
    DateTime OccurredAtUtc);

/// <summary>
/// Hook for reacting to project events. F08 (audit log, notifications) registers an implementation; this feature writes no
/// audit rows itself. Register with <c>services.AddScoped&lt;IProjectEvents, MyHandler&gt;()</c>.
/// </summary>
public interface IProjectEvents
{
    Task HandleAsync(ProjectEvent projectEvent, CancellationToken cancellationToken = default);
}

/// <summary>Fans an event out to every registered <see cref="IProjectEvents"/>. A failing handler is logged, never thrown.</summary>
public sealed class ProjectEventPublisher(IEnumerable<IProjectEvents> handlers, ILogger<ProjectEventPublisher> logger)
{
    public async Task PublishAsync(ProjectEvent projectEvent, CancellationToken cancellationToken = default)
    {
        foreach (var handler in handlers)
        {
            try
            {
                await handler.HandleAsync(projectEvent, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A notification problem must not undo or block a project change that is already saved.
                logger.LogError(ex, "Project event handler {Handler} failed for {EventType}", handler.GetType().Name, projectEvent.Type);
            }
        }
    }
}
