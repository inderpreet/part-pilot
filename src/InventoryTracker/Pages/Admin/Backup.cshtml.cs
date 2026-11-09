using InventoryTracker.Auth;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Admin;

[Authorize(Policy = Policies.RequireAdmin)]
public class BackupModel(IBackupService backup, ICurrentUser currentUser) : PageModel
{
    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        var snapshot = await backup.CreateSnapshotAsync(HttpContext.RequestAborted);
        var stream = new FileStream(snapshot.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.DeleteOnClose);
        await backup.NotifyDownloadedAsync(snapshot.DownloadName, currentUser.Id, HttpContext.RequestAborted);
        return File(stream, "application/vnd.sqlite3", snapshot.DownloadName);
    }
}
