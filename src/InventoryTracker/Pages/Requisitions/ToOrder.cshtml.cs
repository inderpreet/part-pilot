using System.Globalization;
using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Requisitions;

/// <summary>
/// Approved requisition lines waiting to be ordered, grouped by preferred supplier (F13). Each group's checkboxes are named
/// <c>Selected_&lt;group&gt;</c> so one "select all" box can cover one group. The rules live in <see cref="IRequisitionOrderService"/>.
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class ToOrderModel(IRequisitionOrderService ordering, ICurrentUser currentUser) : PageModel
{
    public const string NewDraft = "new";
    public const string NoSupplierKey = "none";

    public ToOrderView View { get; private set; } = null!;

    [BindProperty(SupportsGet = true)]
    public int? PurchaseOrderId { get; set; }

    /// <summary>
    /// Per supplier id: <see cref="NewDraft"/> or the id of one of its drafts. Read from <c>Targets[id]</c> fields by hand: when
    /// none is posted, model binding of a dictionary falls back to every form field (the antiforgery token included) and fails.
    /// </summary>
    public Dictionary<string, string> Targets { get; } = [];

    /// <summary>The supplier picked for a line that has no usable preferred supplier, from <c>SupplierFor[lineId]</c> fields.</summary>
    public Dictionary<long, int?> SupplierFor { get; } = [];

    public HashSet<long> Selected { get; } = [];

    public Dictionary<long, string> RowErrors { get; } = [];

    public Dictionary<int, string> GroupErrors { get; } = [];

    public IReadOnlyList<ConversionOutcome> Outcomes { get; private set; } = [];

    public static string GroupKey(ToOrderGroup group) => group.SupplierId?.ToString(CultureInfo.InvariantCulture) ?? NoSupplierKey;

    public string TargetFor(int supplierId) => Targets.GetValueOrDefault(supplierId.ToString(CultureInfo.InvariantCulture)) ?? NewDraft;

    public async Task<IActionResult> OnGetAsync() => await LoadAsync() ?? Page();

    public async Task<IActionResult> OnPostAsync()
    {
        if (await LoadAsync() is { } gone)
        {
            return gone;
        }

        foreach (var (key, values) in Request.Form)
        {
            if (key.StartsWith("Selected_", StringComparison.Ordinal))
            {
                foreach (var value in values)
                {
                    if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                    {
                        Selected.Add(id);
                    }
                }
            }
            else if (Indexed(key, "Targets") is { } supplierKey)
            {
                Targets[supplierKey] = values.ToString();
            }
            else if (Indexed(key, "SupplierFor") is { } lineKey && long.TryParse(lineKey, NumberStyles.None, CultureInfo.InvariantCulture, out var lineId))
            {
                SupplierFor[lineId] = int.TryParse(values.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out var supplierId) ? supplierId : null;
            }
        }

        var lines = View.Groups.SelectMany(g => g.Lines).ToDictionary(l => l.RequisitionLineId);
        var bySupplier = new Dictionary<(int Supplier, int? Order), List<long>>();
        foreach (var id in Selected)
        {
            if (!lines.TryGetValue(id, out var line))
            {
                ModelState.AddModelError(string.Empty, "A selected line has already been ordered or is no longer approved. The list has been refreshed.");
                continue;
            }

            var supplier = View.ForOrderSupplierId ?? line.GroupSupplierId ?? SupplierFor.GetValueOrDefault(id);
            if (supplier is null)
            {
                RowErrors[id] = "Choose a supplier for this line.";
                continue;
            }

            int? order = PurchaseOrderId ?? (int.TryParse(TargetFor(supplier.Value), NumberStyles.None, CultureInfo.InvariantCulture, out var draft) ? draft : null);
            var key = (supplier.Value, order);
            if (!bySupplier.TryGetValue(key, out var list))
            {
                bySupplier[key] = list = [];
            }

            list.Add(id);
        }

        if (Selected.Count == 0)
        {
            ModelState.AddModelError(string.Empty, "Select at least one line to order.");
        }

        if (!ModelState.IsValid || RowErrors.Count > 0)
        {
            return Page();
        }

        var result = await ordering.ConvertAsync(
            [.. bySupplier.Select(g => new ConversionGroup(g.Key.Supplier, g.Key.Order, g.Value))],
            currentUser.ToRequisitionActor(),
            HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            foreach (var error in result.Errors)
            {
                if (error.Field is { } field && field.StartsWith("Line", StringComparison.Ordinal) && long.TryParse(field[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var lineId))
                {
                    RowErrors[lineId] = error.Message;
                }
                else if (error.Field is { } groupField && groupField.StartsWith("Group", StringComparison.Ordinal) && int.TryParse(groupField[5..], NumberStyles.None, CultureInfo.InvariantCulture, out var supplierId))
                {
                    GroupErrors[supplierId] = error.Message;
                }

                ModelState.AddModelError(string.Empty, error.Message);
            }

            return Page();
        }

        // Done: show what was created and the list as it is now. Re-posting the form is harmless (the lines are linked).
        Outcomes = result.Value;
        Selected.Clear();
        Targets.Clear();
        SupplierFor.Clear();
        await LoadAsync();
        return Page();
    }

    /// <summary>The key inside <c>name[key]</c>, or <c>null</c> for any other field.</summary>
    private static string? Indexed(string field, string name)
        => field.Length > name.Length + 2 && field.StartsWith(name + "[", StringComparison.Ordinal) && field.EndsWith(']')
            ? field[(name.Length + 1)..^1]
            : null;

    private async Task<IActionResult?> LoadAsync()
    {
        var view = await ordering.GetToOrderAsync(PurchaseOrderId, HttpContext.RequestAborted);
        if (view is null)
        {
            this.FlashError("That purchase order is not a draft any more, so lines cannot be added to it.");
            return PurchaseOrderId is { } id ? RedirectToPage("/PurchaseOrders/Detail", new { id }) : RedirectToPage();
        }

        View = view;
        return null;
    }
}
