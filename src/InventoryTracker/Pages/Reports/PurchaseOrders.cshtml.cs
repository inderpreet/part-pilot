using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Reports;

/// <summary>Purchase orders by order date, supplier and status, with what is still to arrive and the spend per supplier (F06 FR-7).</summary>
[Authorize(Policy = Policies.CanView)]
public class PurchaseOrdersModel(IReportService reports, ISupplierService suppliers, IClock clock) : PageModel
{
    public PurchaseOrderReportFilter Query { get; private set; } = new();

    public PurchaseOrderReport Report { get; private set; } = null!;

    public IReadOnlyList<SupplierLookupItem> Suppliers { get; private set; } = [];

    /// <summary>The local calendar date, to flag open orders past their expected date.</summary>
    public DateOnly Today { get; } = DateOnly.FromDateTime(clock.UtcNow.ToLocalTime());

    public bool IsFiltered => Query.SearchTerm is not null || Query.SupplierId is not null || Query.Status is not null || Query.From is not null || Query.To is not null;

    public async Task<IActionResult> OnGetAsync([FromQuery] PurchaseOrderReportFilter query)
    {
        Query = query;
        Report = await reports.GetPurchaseOrdersAsync(query, HttpContext.RequestAborted);

        if (this.IsHtmx())
        {
            return Partial("_PurchaseOrdersResults", this);
        }

        Suppliers = await suppliers.LookupAsync(null, activeOnly: false, take: SupplierService.MaxLookupTake, cancellationToken: HttpContext.RequestAborted);
        return Page();
    }

    public IActionResult OnGetCsv([FromQuery] PurchaseOrderReportFilter query)
        => new CsvResult(ReportPageExtensions.CsvFileName("purchase-orders", clock), (stream, ct) => reports.WritePurchaseOrdersCsvAsync(query, stream, ct));
}
