using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Export;

[Authorize(Policy = Policies.CanExport)]
public class IndexModel(IExportService exports, IAuthorizationService authorization, IClock clock) : PageModel
{
    public bool CanExportData { get; private set; }

    public bool CanExportBoms { get; private set; }

    public IReadOnlyList<BomExportOption> Projects { get; private set; } = [];

    public int? ProjectId { get; private set; }

    public bool IncludeArchived { get; private set; }

    public async Task OnGetAsync(int? projectId, bool includeArchived = false)
    {
        CanExportData = (await authorization.AuthorizeAsync(User, Policies.CanExportData)).Succeeded;
        CanExportBoms = (await authorization.AuthorizeAsync(User, Policies.CanExportBoms)).Succeeded;
        ProjectId = projectId;
        IncludeArchived = includeArchived;
        if (CanExportBoms)
        {
            Projects = await exports.ListBomProjectsAsync(HttpContext.RequestAborted);
        }
    }

    public async Task<IActionResult> OnGetProductsAsync()
        => await Csv(Policies.CanExportData, "parts", (s, ct) => exports.WriteProductsAsync(s, ct));

    public async Task<IActionResult> OnGetSuppliersAsync()
        => await Csv(Policies.CanExportData, "suppliers", (s, ct) => exports.WriteSuppliersAsync(s, ct));

    public async Task<IActionResult> OnGetLinksAsync()
        => await Csv(Policies.CanExportData, "part-suppliers", (s, ct) => exports.WriteProductSuppliersAsync(s, ct));

    public async Task<IActionResult> OnGetTransactionsAsync(DateOnly? from, DateOnly? to, int? tz)
    {
        var (fromUtc, toUtcExclusive) = LocalTimeInput.DayRange(from, to, tz);
        return await Csv(Policies.CanExportData, "stock-transactions", (s, ct) => exports.WriteTransactionsAsync(s, fromUtc, toUtcExclusive, ct));
    }

    public async Task<IActionResult> OnGetBomsAsync(int? projectId, bool includeArchived)
        => await Csv(Policies.CanExportBoms, "boms", (s, ct) => exports.WriteBomsAsync(s, new BomExportQuery(projectId, includeArchived), ct));

    private async Task<IActionResult> Csv(string policy, string name, Func<Stream, CancellationToken, Task> write)
    {
        if (!(await authorization.AuthorizeAsync(User, policy)).Succeeded)
        {
            return Forbid();
        }

        return new CsvResult(ReportPageExtensions.CsvFileName(name, clock), write);
    }
}
