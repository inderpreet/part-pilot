using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.PurchaseOrders;

/// <summary>
/// One order: header, lines, receipts and the lifecycle buttons. The page needs only <c>CanView</c>; every action
/// handler checks <c>CanEdit</c> itself. Each action works as a plain form post and, with htmx, as <c>hx-post</c> +
/// <c>hx-confirm</c>; either way it ends on the refreshed page (the message rides TempData, which an <c>HX-Redirect</c> can use).
/// </summary>
[Authorize(Policy = Policies.CanView)]
public class DetailModel(IPurchaseOrderService orders, ICurrentUser currentUser, IClock clock) : PageModel
{
    public PurchaseOrderDetail Order { get; private set; } = null!;

    public IReadOnlyList<PurchaseOrderReceiptItem> Receipts { get; private set; } = [];

    /// <summary>Only used to hide edit actions; the handlers and the pages behind them enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    public DateOnly Today { get; } = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());

    public bool IsOverdue => PurchaseOrderDisplay.IsOverdue(Order.Status, Order.ExpectedDate, Today);

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var order = await orders.GetAsync(id, HttpContext.RequestAborted);
        if (order is null)
        {
            return NotFound();
        }

        Order = order;
        Receipts = await orders.GetReceiptsAsync(id, HttpContext.RequestAborted);
        return Page();
    }

    public Task<IActionResult> OnPostSubmitAsync(int id, long rowVersion)
        => ActAsync(id, async actor => await orders.SubmitAsync(id, actor, rowVersion, HttpContext.RequestAborted), "Order {0} submitted.");

    public Task<IActionResult> OnPostCancelAsync(int id, long rowVersion)
        => ActAsync(id, async actor => await orders.CancelAsync(id, actor, rowVersion, HttpContext.RequestAborted), "Order {0} cancelled.");

    public Task<IActionResult> OnPostCloseShortAsync(int id, long rowVersion)
        => ActAsync(id, async actor => await orders.CloseShortAsync(id, actor, rowVersion, HttpContext.RequestAborted), "Order {0} closed short; the rest will not be received.");

    public Task<IActionResult> OnPostDeleteAsync(int id, long rowVersion)
        => ActAsync(id, async actor => await orders.DeleteDraftAsync(id, actor, rowVersion, HttpContext.RequestAborted), "Draft {0} deleted.", toListOnSuccess: true);

    public Task<IActionResult> OnPostReverseAsync(int id, long transactionId)
        => ActAsync(id, async actor => await orders.ReverseReceiptAsync(transactionId, actor, cancellationToken: HttpContext.RequestAborted), "Receipt #{1} reversed; stock and the order were corrected.", transactionId);

    private async Task<IActionResult> ActAsync(int id, Func<PurchaseOrderActor, Task<Result>> action, string success, long transactionId = 0, bool toListOnSuccess = false)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var order = await orders.GetAsync(id, HttpContext.RequestAborted);
        if (order is null)
        {
            return NotFound();
        }

        var result = await action(new PurchaseOrderActor(currentUser.Id!, currentUser.IsAdmin));
        if (result.Succeeded)
        {
            TempData[PageExtensions.FlashKey] = string.Format(System.Globalization.CultureInfo.InvariantCulture, success, order.Number, transactionId);
        }
        else
        {
            TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        }

        var target = result.Succeeded && toListOnSuccess
            ? Url.Page("Index")!
            : Url.Page("Detail", new { id })!;

        return this.IsHtmx() ? this.HxRedirect(target) : LocalRedirect(target);
    }
}
