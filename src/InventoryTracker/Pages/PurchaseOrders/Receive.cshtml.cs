using System.ComponentModel.DataAnnotations;
using System.Globalization;
using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.PurchaseOrders;

/// <summary>
/// Receiving goods against a submitted / partially received order: one quantity per line (default: what is outstanding),
/// a delivery note reference and a note. Posting creates the stock receipts through <see cref="IPurchaseOrderService"/>.
/// Receiving more than ordered is an Admin power and needs an explicit tick.
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class ReceiveModel(IPurchaseOrderService orders, ICurrentUser currentUser) : PageModel
{
    [BindProperty]
    public ReceiveForm Input { get; set; } = new();

    public PurchaseOrderDetail Order { get; private set; } = null!;

    /// <summary>Only admins are offered the over-receipt tick (the service refuses it for anyone else anyway).</summary>
    public bool CanOverReceive => currentUser.IsAdmin;

    /// <summary>True when the last attempt was refused for receiving more than is outstanding, so an admin can tick the box and retry.</summary>
    public bool OverReceiptRefused { get; private set; }

    /// <summary>True when the receipt failed because someone else changed the order first.</summary>
    public bool Conflict { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var order = await orders.GetAsync(id, HttpContext.RequestAborted);
        if (order is null)
        {
            return NotFound();
        }

        if (!order.CanReceive)
        {
            TempData[PageExtensions.FlashErrorKey] = $"Order {order.Number} is {PurchaseOrderDisplay.StatusLabel(order.Status).ToLowerInvariant()}; nothing can be received against it.";
            return RedirectToPage("Detail", new { id });
        }

        Order = order;
        Input = new ReceiveForm
        {
            RowVersion = order.RowVersion,
            Lines = [.. order.Lines.Select(l => new ReceiveLineForm { LineId = l.Id, Quantity = l.Outstanding.ToString(CultureInfo.InvariantCulture) })],
        };
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id)
    {
        var order = await orders.GetAsync(id, HttpContext.RequestAborted);
        if (order is null)
        {
            return NotFound();
        }

        Order = order;

        // Whatever the form says, the lines are the order's lines, in the order's order: a missing or foreign id cannot slip through.
        var posted = new Dictionary<long, ReceiveLineForm>();
        foreach (var line in Input.Lines)
        {
            posted.TryAdd(line.LineId, line);
        }

        Input.Lines = [.. order.Lines.Select(l => posted.TryGetValue(l.Id, out var form) ? form : new ReceiveLineForm { LineId = l.Id })];

        var inputs = new List<ReceiveLineInput>();
        for (var i = 0; i < Input.Lines.Count; i++)
        {
            var text = Input.Lines[i].Quantity?.Trim();
            var quantity = 0;
            if (!string.IsNullOrEmpty(text) && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity))
            {
                ModelState.AddModelError($"{nameof(Input)}.Lines[{i}].Quantity", $"{order.Lines[i].Sku}: enter a whole number of units, or leave it blank for none.");
            }

            inputs.Add(new ReceiveLineInput(Input.Lines[i].LineId, quantity));
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await orders.ReceiveAsync(
            id,
            inputs,
            new PurchaseOrderActor(currentUser.Id!, currentUser.IsAdmin),
            Input.Reference,
            Input.Note,
            allowOverReceipt: Input.AllowOverReceipt,
            rowVersion: Input.RowVersion,
            cancellationToken: HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            Conflict = result.Errors.Any(e => e.Code == "po.conflict");
            OverReceiptRefused = result.Errors.Any(e => e.Code == "po.over_receipt");
            ModelState.AddStockErrors(result, nameof(Input), keepLineIndex: true, lineLabel: index => $"{order.Lines[Math.Clamp(index, 0, order.Lines.Count - 1)].Sku}: ");
            return Page();
        }

        var receipt = result.Value;
        TempData[PageExtensions.FlashKey] =
            $"Received {receipt.UnitsReceived} unit{(receipt.UnitsReceived == 1 ? "" : "s")} on {receipt.LinesReceived} line{(receipt.LinesReceived == 1 ? "" : "s")} of {order.Number}. "
            + $"The order is now {PurchaseOrderDisplay.StatusLabel(receipt.Status).ToLowerInvariant()}.";
        return RedirectToPage("Detail", new { id });
    }

    public sealed class ReceiveForm
    {
        public List<ReceiveLineForm> Lines { get; set; } = [];

        [Display(Name = "Delivery note"), StringLength(100, ErrorMessage = "The reference can be at most 100 characters.")]
        public string? Reference { get; set; }

        [StringLength(500, ErrorMessage = "The note can be at most 500 characters.")]
        public string? Note { get; set; }

        public bool AllowOverReceipt { get; set; }

        /// <summary>The version the form was loaded with; a stale one is reported as a conflict.</summary>
        public long RowVersion { get; set; }
    }

    public sealed class ReceiveLineForm
    {
        public long LineId { get; set; }

        /// <summary>Text, so a typo gets a friendly message instead of a binder error; blank means none.</summary>
        public string? Quantity { get; set; }
    }
}
