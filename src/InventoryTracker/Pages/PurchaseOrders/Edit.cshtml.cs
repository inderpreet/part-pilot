using System.ComponentModel.DataAnnotations;
using System.Globalization;
using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.PurchaseOrders;

/// <summary>What the lines block (<c>_PoLines</c>) shows: the order, plus what was typed into the add-line form when it failed.</summary>
public sealed record PoLinesModel(PurchaseOrderDetail Order, int? ProductId = null, string? ProductLabel = null, string? Quantity = null, string? UnitCost = null);

/// <summary>The dropdown of the add-line product picker: the supplier's products first, with the price a line would get.</summary>
public sealed record PoProductResultsModel(IReadOnlyList<PurchaseOrderProductChoice> Items, string? Query)
{
    public bool IsExact(PurchaseOrderProductChoice item)
        => !string.IsNullOrEmpty(Query) && string.Equals(item.Sku, Query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Creates a draft (no id) or edits an order. A draft's supplier, dates and notes are the header form; its lines are edited
/// in place through htmx handlers that each answer with the whole lines block. Once submitted only the expected date and
/// notes can change, and there are no lines to edit. Page handlers only bind, call the service and render.
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class EditModel(IPurchaseOrderService orders, ISupplierService suppliers, ICurrentUser currentUser, IClock clock) : PageModel
{
    [BindProperty]
    public HeaderForm Input { get; set; } = new();

    public PurchaseOrderDetail? Order { get; private set; }

    public IReadOnlyList<SupplierLookupItem> Suppliers { get; private set; } = [];

    public bool IsEdit => Order is not null;

    /// <summary>True when the save failed because someone else changed the order first; the page offers a reload link.</summary>
    public bool Conflict { get; private set; }

    /// <summary>The supplier cannot change once the order has lines (or is submitted).</summary>
    public bool SupplierLocked => Order is { } o && (o.Lines.Count > 0 || !o.CanEditLines);

    public DateOnly Today => DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());

    private PurchaseOrderActor Actor => new(currentUser.Id!, currentUser.IsAdmin);

    public async Task<IActionResult> OnGetAsync(int? id, int? supplierId)
    {
        if (id is null)
        {
            Input = new HeaderForm { SupplierId = supplierId, OrderDate = Today };
            await LoadSuppliersAsync(null);
            return Page();
        }

        Order = await orders.GetAsync(id.Value, HttpContext.RequestAborted);
        if (Order is null)
        {
            return NotFound();
        }

        if (!Order.CanEditHeader)
        {
            TempData[PageExtensions.FlashErrorKey] = $"Order {Order.Number} is {StatusText(Order.Status)} and can no longer be edited.";
            return RedirectToPage("Detail", new { id });
        }

        Input = HeaderForm.From(Order);
        await LoadSuppliersAsync(Order);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        if (id is not null)
        {
            Order = await orders.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Order is null)
            {
                return NotFound();
            }
        }

        // Pre-check the cross-field rule so a failed create never leaves a half-made draft behind.
        if (Input.OrderDate is { } ordered && Input.ExpectedDate is { } expected && expected < ordered && Order is not { CanEditLines: false })
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.ExpectedDate)}", "The expected date cannot be before the order date.");
        }

        if (!ModelState.IsValid)
        {
            await LoadSuppliersAsync(Order);
            return Page();
        }

        return Order is null ? await CreateAsync() : await UpdateAsync(Order);
    }

    private async Task<IActionResult> CreateAsync()
    {
        if (Input.SupplierId is not { } supplierId)
        {
            ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.SupplierId)}", "Choose a supplier.");
            await LoadSuppliersAsync(null);
            return Page();
        }

        var created = await orders.CreateDraftAsync(supplierId, Actor, orderDate: Input.OrderDate, cancellationToken: HttpContext.RequestAborted);
        if (!created.Succeeded)
        {
            ModelState.AddErrors(created, nameof(Input));
            await LoadSuppliersAsync(null);
            return Page();
        }

        var draft = created.Value;
        if (Input.ExpectedDate is not null || !string.IsNullOrWhiteSpace(Input.Notes))
        {
            var update = await orders.UpdateHeaderAsync(
                draft.Id, draft.RowVersion, new PurchaseOrderHeaderInput(draft.SupplierId, draft.OrderDate, Input.ExpectedDate, Input.Notes), HttpContext.RequestAborted);
            if (!update.Succeeded)
            {
                // The draft exists now; carry on to its editor and say what was not saved.
                TempData[PageExtensions.FlashErrorKey] = "The draft was created, but the dates and notes were not saved: " + string.Join(" ", update.Errors.Select(e => e.Message));
                return RedirectToPage("Edit", new { id = draft.Id });
            }
        }

        TempData[PageExtensions.FlashKey] = $"Draft {draft.Number} created. Add the parts to order below.";
        return RedirectToPage("Edit", new { id = draft.Id });
    }

    private async Task<IActionResult> UpdateAsync(PurchaseOrderDetail existing)
    {
        // Only a draft without lines may change supplier or order date; otherwise keep what is stored (a disabled input posts nothing).
        var supplierId = SupplierLocked ? existing.SupplierId : Input.SupplierId ?? existing.SupplierId;
        var orderDate = existing.CanEditLines ? Input.OrderDate ?? existing.OrderDate : existing.OrderDate;

        var result = await orders.UpdateHeaderAsync(
            existing.Id, Input.RowVersion, new PurchaseOrderHeaderInput(supplierId, orderDate, Input.ExpectedDate, Input.Notes), HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            Conflict = result.Errors.Any(e => e.Code == "po.conflict");
            ModelState.AddErrors(result, nameof(Input));
            await LoadSuppliersAsync(existing);
            return Page();
        }

        TempData[PageExtensions.FlashKey] = $"Order {existing.Number} saved.";
        return existing.CanEditLines
            ? RedirectToPage("Edit", new { id = existing.Id })
            : RedirectToPage("Detail", new { id = existing.Id });
    }

    // ---------- Lines (htmx): each handler answers with the whole lines block ----------

    /// <summary>The add-line picker's dropdown: the order's supplier's products first, then the rest of the catalog.</summary>
    public async Task<IActionResult> OnGetProductsAsync(int id, string? q)
    {
        var order = await orders.GetAsync(id, HttpContext.RequestAborted);
        if (order is null)
        {
            return NotFound();
        }

        var items = await orders.LookupProductsAsync(order.SupplierId, q, id, cancellationToken: HttpContext.RequestAborted);
        return Partial("_PoProductResults", new PoProductResultsModel(items, q?.Trim()));
    }

    public async Task<IActionResult> OnPostAddLineAsync(int id, int? productId, string? q, string? quantity, string? unitCost)
    {
        long? cents = null;
        if (!string.IsNullOrWhiteSpace(unitCost))
        {
            if (MoneyParser.TryParseCents(unitCost, out var parsed))
            {
                cents = parsed;
            }
            else
            {
                ModelState.AddModelError(PurchaseOrderFields.UnitCost, "Enter an amount like 12.50 (at most two decimal places), or leave it blank for the usual price.");
            }
        }

        var quantityValue = 0;
        if (!int.TryParse(quantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out quantityValue))
        {
            ModelState.AddModelError(PurchaseOrderFields.Quantity, "Quantity must be a whole number, 1 or more.");
        }

        if (productId is null)
        {
            ModelState.AddModelError(PurchaseOrderFields.ProductId, "Choose a part.");
        }

        if (ModelState.IsValid)
        {
            var result = await orders.AddLineAsync(id, productId!.Value, quantityValue, cents, HttpContext.RequestAborted);
            if (result.Succeeded)
            {
                this.Flash($"Added {result.Value.Sku} to the order.");
                return await LinesAsync(id);
            }

            ModelState.AddErrors(result);
        }

        return await LinesAsync(id, productId, q, quantity, unitCost);
    }

    public async Task<IActionResult> OnPostSaveLineAsync(int id, long lineId, string? quantity, string? unitCost)
    {
        var key = LineKey(lineId);
        var quantityOk = int.TryParse(quantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantityValue);
        if (!quantityOk)
        {
            ModelState.AddModelError($"{key}.{PurchaseOrderFields.Quantity}", "Quantity must be a whole number, 1 or more.");
        }

        var costOk = MoneyParser.TryParseCents(unitCost, out var cents);
        if (!costOk)
        {
            ModelState.AddModelError($"{key}.{PurchaseOrderFields.UnitCost}", "Enter a unit cost like 12.50 (at most two decimal places).");
        }

        if (quantityOk && costOk)
        {
            var result = await orders.UpdateLineAsync(id, lineId, quantityValue, cents, HttpContext.RequestAborted);
            if (!result.Succeeded)
            {
                ModelState.AddErrors(result, key);
            }
        }

        return await LinesAsync(id);
    }

    public async Task<IActionResult> OnPostRemoveLineAsync(int id, long lineId)
    {
        var result = await orders.RemoveLineAsync(id, lineId, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            this.Flash("Line removed.");
        }
        else
        {
            ModelState.AddErrors(result, LineKey(lineId));
        }

        return await LinesAsync(id);
    }

    public static string LineKey(long lineId) => $"line{lineId}";

    /// <summary>
    /// The lines block for htmx; failures (ModelState not valid) answer 422 so the swap shows the messages.
    /// Without htmx the messages ride TempData to the editor.
    /// </summary>
    private async Task<IActionResult> LinesAsync(int id, int? productId = null, string? productLabel = null, string? quantity = null, string? unitCost = null)
    {
        var order = await orders.GetAsync(id, HttpContext.RequestAborted);
        if (order is null)
        {
            return NotFound();
        }

        if (!this.IsHtmx())
        {
            if (!ModelState.IsValid)
            {
                TempData[PageExtensions.FlashErrorKey] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            }

            return RedirectToPage("Edit", new { id });
        }

        if (!ModelState.IsValid)
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }

        return Partial("_PoLines", new PoLinesModel(order, productId, productLabel, quantity, unitCost));
    }

    private async Task LoadSuppliersAsync(PurchaseOrderDetail? existing)
    {
        // New orders offer active suppliers only; an existing order's own supplier is always listed, even if archived since.
        var active = await suppliers.LookupAsync(null, activeOnly: true, take: SupplierService.MaxLookupTake, cancellationToken: HttpContext.RequestAborted);
        if (existing is not null && active.All(s => s.Id != existing.SupplierId))
        {
            active = [.. active, new SupplierLookupItem(existing.SupplierId, existing.SupplierName, existing.SupplierContactName, false)];
        }

        Suppliers = active;
    }

    private static string StatusText(PurchaseOrderStatus status) => PurchaseOrderDisplay.StatusLabel(status).ToLowerInvariant();

    public sealed class HeaderForm
    {
        [Display(Name = "Supplier")]
        public int? SupplierId { get; set; }

        [Display(Name = "Order date")]
        public DateOnly? OrderDate { get; set; }

        [Display(Name = "Expected date")]
        public DateOnly? ExpectedDate { get; set; }

        [StringLength(2000, ErrorMessage = "Notes can be at most 2000 characters.")]
        public string? Notes { get; set; }

        /// <summary>The version the form was loaded with; a stale one is reported as an edit conflict.</summary>
        public long RowVersion { get; set; }

        public static HeaderForm From(PurchaseOrderDetail o) => new()
        {
            SupplierId = o.SupplierId,
            OrderDate = o.OrderDate,
            ExpectedDate = o.ExpectedDate,
            Notes = o.Notes,
            RowVersion = o.RowVersion,
        };
    }
}
