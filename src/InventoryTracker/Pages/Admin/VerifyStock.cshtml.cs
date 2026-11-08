using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Admin;

/// <summary>
/// Recomputes every product's on-hand quantity from the ledger and compares it with the cached value
/// (architecture invariant 1). A mismatch means something bypassed <c>StockService</c>; "Fix" rewrites the cache from the ledger.
/// </summary>
[Authorize(Policy = Policies.RequireAdmin)]
public class VerifyStockModel(IStockService stock) : PageModel
{
    private const int SkusInMessage = 10;

    public StockVerifyReport? Report { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostCheckAsync()
    {
        Report = await stock.VerifyAsync(fix: false, HttpContext.RequestAborted);
        return Page();
    }

    /// <summary>Repairs, then redirects back with a message (works as a plain post and as <c>hx-post</c> + <c>hx-confirm</c>).</summary>
    public async Task<IActionResult> OnPostFixAsync()
    {
        var report = await stock.VerifyAsync(fix: true, HttpContext.RequestAborted);

        if (report.Fixed)
        {
            var skus = string.Join(", ", report.Mismatches.Take(SkusInMessage).Select(m => m.Sku));
            var more = report.Mismatches.Count > SkusInMessage ? $" and {report.Mismatches.Count - SkusInMessage} more" : string.Empty;
            TempData[PageExtensions.FlashKey] =
                $"Repaired {report.Mismatches.Count} {(report.Mismatches.Count == 1 ? "part" : "parts")} from the ledger: {skus}{more}.";
        }
        else
        {
            TempData[PageExtensions.FlashKey] = "Nothing to repair: every part already matches its ledger.";
        }

        var url = Url.Page("VerifyStock")!;
        return this.IsHtmx() ? this.HxRedirect(url) : Redirect(url);
    }
}
