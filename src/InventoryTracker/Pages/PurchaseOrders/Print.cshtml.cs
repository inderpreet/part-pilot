using InventoryTracker.Auth;
using InventoryTracker.Data;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.PurchaseOrders;

/// <summary>The printable order: no navigation, laid out for A4 / Letter. It opens the print dialog itself (see <c>_PrintLayout</c>).</summary>
[Authorize(Policy = Policies.CanView)]
public class PrintModel(IPurchaseOrderService orders, ISettingsService settings) : PageModel
{
    public PurchaseOrderDetail Order { get; private set; } = null!;

    public string CompanyName { get; private set; } = string.Empty;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        var order = await orders.GetAsync(id, HttpContext.RequestAborted);
        if (order is null)
        {
            return NotFound();
        }

        Order = order;
        CompanyName = await settings.GetStringAsync(SettingKeys.CompanyName, HttpContext.RequestAborted);
        return Page();
    }
}
