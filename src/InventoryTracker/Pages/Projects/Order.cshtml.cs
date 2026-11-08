using System.Globalization;
using System.Text.Json;
using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Projects;

/// <summary>
/// Review of the parts a project is short of, and what to do with each: add it to a purchase order, change its quantity on a
/// draft that already holds it, or leave it. A normal page (a table and two selects), not a dialog. Nothing is ever submitted
/// from here, and Draft orders are information only: they change the suggested actions, never what is short.
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class OrderModel(
    IProjectService projects,
    IPurchaseOrderService orders,
    ISupplierService suppliers,
    ICurrentUser currentUser) : PageModel
{
    public const string ResultKey = "ProjectOrderResult";

    public const string AddAction = "Add";
    public const string UpdateAction = "Update";
    public const string SkipAction = "Skip";

    [BindProperty]
    public OrderForm Input { get; set; } = new();

    public ProjectDetail Project { get; private set; } = null!;

    public BomStatus Status { get; private set; } = null!;

    /// <summary>Every Draft order: the only ones parts can be added to or changed on.</summary>
    public IReadOnlyList<DraftOption> Drafts { get; private set; } = [];

    public IReadOnlyList<SupplierLookupItem> Suppliers { get; private set; } = [];

    public IReadOnlyList<OrderLineView> Lines { get; private set; } = [];

    /// <summary>Where parts set to Add go: an existing draft, or a new draft for a supplier.</summary>
    public TargetKind Kind { get; private set; }

    public int? TargetOrderId { get; private set; }

    public int? TargetSupplierId { get; private set; }

    /// <summary>Set when the page shows what a post did (after the redirect), instead of the form.</summary>
    public AddShortagesResult? Result { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, [FromQuery(Name = "Input")] TargetChoice? target)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Choose(target?.Kind, target?.PurchaseOrderId, target?.SupplierId);
        await BuildLinesAsync(posted: null);
        return Page();
    }

    /// <summary>Just the lines (htmx), when the target changes: the suggested quantities depend on the supplier's minimum order quantity.</summary>
    public async Task<IActionResult> OnGetLinesAsync(int id, [FromQuery(Name = "Input")] TargetChoice? target)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Choose(target?.Kind, target?.PurchaseOrderId, target?.SupplierId);
        await BuildLinesAsync(posted: null);
        return Partial("_OrderLines", this);
    }

    public async Task<IActionResult> OnGetResultAsync(int id)
    {
        var json = TempData[ResultKey] as string;
        AddShortagesResult? result = null;
        if (!string.IsNullOrEmpty(json))
        {
            try
            {
                result = JsonSerializer.Deserialize<AddShortagesResult>(json);
            }
            catch (JsonException)
            {
                result = null;
            }
        }

        if (result is null)
        {
            // A refresh after the result was shown: nothing to show, back to the project.
            return RedirectToPage("Detail", new { id });
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Result = result;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Choose(Input.Kind, Input.PurchaseOrderId, Input.SupplierId);

        var decisions = new List<ShortageDecision>();
        var postedLines = Input.Lines.ToDictionary(l => l.LineId, l => l);
        for (var i = 0; i < Input.Lines.Count; i++)
        {
            var line = Input.Lines[i];
            var sku = Status.Lines.FirstOrDefault(l => l.LineId == line.LineId)?.Sku ?? "part";
            var action = line.Action switch
            {
                AddAction => ShortageAction.Add,
                UpdateAction => ShortageAction.UpdateQuantity,
                _ => ShortageAction.Skip,
            };

            var quantity = 0;
            if (action != ShortageAction.Skip
                && (!int.TryParse(line.Quantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity) || quantity < 1))
            {
                ModelState.AddModelError($"Input.Lines[{i}].Quantity", $"{sku}: enter a quantity of 1 or more.");
                continue;
            }

            if (action == ShortageAction.UpdateQuantity && line.DraftId is null)
            {
                ModelState.AddModelError($"Input.Lines[{i}].DraftId", $"{sku}: choose which draft order to change.");
                continue;
            }

            decisions.Add(new ShortageDecision(line.LineId, action, quantity, action == ShortageAction.UpdateQuantity ? line.DraftId : null));
        }

        ShortageTarget? target = null;
        if (decisions.Any(d => d.Action == ShortageAction.Add))
        {
            target = Kind == TargetKind.Existing
                ? (Input.PurchaseOrderId is { } order ? ShortageTarget.ExistingDraft(order) : null)
                : (Input.SupplierId is { } supplier ? ShortageTarget.NewDraft(supplier) : null);
            if (target is null)
            {
                ModelState.AddModelError("Input.Target", Kind == TargetKind.Existing ? "Choose the draft order to add the parts to." : "Choose the supplier for the new draft order.");
            }
        }

        if (ModelState.IsValid)
        {
            var result = await projects.AddShortagesToOrderAsync(id, decisions, target, new PurchaseOrderActor(currentUser.Id!, currentUser.IsAdmin), HttpContext.RequestAborted);
            if (result.Succeeded)
            {
                TempData[ResultKey] = JsonSerializer.Serialize(result.Value);
                TempData[PageExtensions.FlashKey] = Summary(result.Value);
                return RedirectToPage("Order", "Result", new { id });
            }

            ModelState.AddErrors(result);
            if (result.Errors.Any(e => e.Code is "po.not_editable"))
            {
                ModelState.AddModelError(string.Empty, "A purchase order changed while this page was open. The drafts below are the current ones; check your choices and try again.");
            }

            // The drafts may have changed since the page opened: show the current ones, keep the user's choices where they still apply.
            await LoadAsync(id);
            Choose(Input.Kind, Input.PurchaseOrderId, Input.SupplierId);
        }

        await BuildLinesAsync(postedLines);
        if (this.IsHtmx())
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
            return Partial("_OrderForm", this);
        }

        return Page();
    }

    public static string Summary(AddShortagesResult result)
    {
        var added = result.Added.Count();
        var updated = result.Updated.Count();
        var skipped = result.Skipped.Count();
        var text = $"Added {added} {(added == 1 ? "line" : "lines")}, updated {updated}, skipped {skipped}.";
        return result.CreatedPurchaseOrderNumber is { } number ? $"Created draft {number}. {text}" : text;
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private async Task<bool> LoadAsync(int id)
    {
        var project = await projects.GetAsync(id, HttpContext.RequestAborted);
        var status = project is null ? null : await projects.GetStatusAsync(id, HttpContext.RequestAborted);
        if (project is null || status is null)
        {
            return false;
        }

        Project = project;
        Status = status;

        var drafts = await orders.SearchAsync(
            new PurchaseOrderListQuery { Tab = PurchaseOrderTab.Draft, PageSize = ListQuery.MaxPageSize, Sort = "number" },
            HttpContext.RequestAborted);
        Drafts = [.. drafts.Items.Select(o => new DraftOption(o.Id, o.Number, o.SupplierId, o.SupplierName, o.LineCount))];
        Suppliers = await suppliers.LookupAsync(null, activeOnly: true, take: SupplierService.MaxLookupTake, cancellationToken: HttpContext.RequestAborted);
        return true;
    }

    /// <summary>Settles which target is shown: the one asked for, else existing drafts when there are any, else a new draft.</summary>
    private void Choose(TargetKind? kind, int? purchaseOrderId, int? supplierId)
    {
        Kind = kind ?? (Drafts.Count > 0 ? TargetKind.Existing : TargetKind.New);
        TargetOrderId = Drafts.Any(d => d.Id == purchaseOrderId) ? purchaseOrderId : null;
        TargetSupplierId = Suppliers.Any(s => s.Id == supplierId) ? supplierId : null;
    }

    /// <summary>
    /// One view per line that still needs ordering. Defaults follow the drafts: no draft holds the part, so Add; drafts cover what
    /// is missing, so Skip; drafts cover part of it, so Update quantity to the current quantity plus what is still uncovered.
    /// Suggested quantities are raised to the minimum order quantity of the supplier the part would end up with.
    /// </summary>
    private async Task BuildLinesAsync(IReadOnlyDictionary<long, OrderLineForm>? posted)
    {
        var targetSupplier = Kind == TargetKind.Existing
            ? Drafts.FirstOrDefault(d => d.Id == TargetOrderId)?.SupplierId
            : TargetSupplierId;

        var views = new List<OrderLineView>();
        foreach (var line in Status.Shortages)
        {
            var minimums = (await suppliers.GetForProductAsync(line.ProductId, HttpContext.RequestAborted))
                .ToDictionary(l => l.SupplierId, l => l.MinOrderQuantity);
            int? MinFor(int? supplierId) => supplierId is { } s && minimums.TryGetValue(s, out var min) ? min : null;

            var held = line.DraftOrders;
            var holdsTarget = Kind == TargetKind.Existing && TargetOrderId is { } t && held.Any(h => h.PurchaseOrderId == t);
            var pick = held.FirstOrDefault(h => h.PurchaseOrderId == TargetOrderId) ?? held.FirstOrDefault();
            var toOrder = (int)Math.Min(line.ToOrder, int.MaxValue);
            var uncovered = (int)Math.Min(line.UncoveredByDrafts, int.MaxValue);

            string action;
            int? draftId = pick?.PurchaseOrderId;
            int quantity;
            if (held.Count == 0)
            {
                action = AddAction;
                quantity = MinOrderRounding.RoundUp(toOrder, MinFor(targetSupplier));
            }
            else if (uncovered == 0)
            {
                action = SkipAction;
                quantity = pick!.Quantity;
            }
            else
            {
                action = UpdateAction;
                var pickSupplier = Drafts.FirstOrDefault(d => d.Id == pick!.PurchaseOrderId)?.SupplierId;
                quantity = MinOrderRounding.RoundUp(pick!.Quantity + uncovered, MinFor(pickSupplier));
            }

            var view = new OrderLineView(line, action, quantity.ToString(CultureInfo.InvariantCulture), draftId, CanAdd: !holdsTarget, CanUpdate: held.Count > 0);

            if (posted is not null && posted.TryGetValue(line.LineId, out var choice))
            {
                var chosenAction = choice.Action is AddAction or UpdateAction or SkipAction ? choice.Action! : action;
                if (chosenAction == AddAction && !view.CanAdd)
                {
                    chosenAction = view.CanUpdate ? UpdateAction : SkipAction;
                }

                if (chosenAction == UpdateAction && !view.CanUpdate)
                {
                    chosenAction = SkipAction;
                }

                view = view with
                {
                    Action = chosenAction,
                    Quantity = choice.Quantity ?? view.Quantity,
                    DraftId = held.Any(h => h.PurchaseOrderId == choice.DraftId) ? choice.DraftId : view.DraftId,
                };
            }

            views.Add(view);
        }

        Lines = views;
    }

    public enum TargetKind
    {
        Existing,
        New,
    }

    /// <summary>The target as chosen in the form, read from the query when the lines are refreshed.</summary>
    public sealed class TargetChoice
    {
        public TargetKind? Kind { get; set; }

        public int? PurchaseOrderId { get; set; }

        public int? SupplierId { get; set; }
    }

    public sealed class OrderForm
    {
        public TargetKind? Kind { get; set; }

        public int? PurchaseOrderId { get; set; }

        public int? SupplierId { get; set; }

        public List<OrderLineForm> Lines { get; set; } = [];
    }

    public sealed class OrderLineForm
    {
        public long LineId { get; set; }

        public string? Action { get; set; }

        public string? Quantity { get; set; }

        public int? DraftId { get; set; }
    }
}

/// <summary>A Draft order a part can be added to.</summary>
public sealed record DraftOption(int Id, string Number, int SupplierId, string SupplierName, int LineCount);

/// <summary>One row of the review: the line's numbers and the action, quantity and draft the form starts with.</summary>
public sealed record OrderLineView(BomLineStatus Line, string Action, string Quantity, int? DraftId, bool CanAdd, bool CanUpdate)
{
    public bool Archived => !Line.ProductIsActive;
}
