using System.Globalization;
using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Projects;

/// <summary>
/// The project page: header, the BOM as a status table, and the totals. Reading needs <c>CanView</c> (costs included);
/// every handler that changes data checks <c>CanEdit</c> itself, and "Change number" checks <c>RequireAdmin</c>.
/// </summary>
[Authorize(Policy = Policies.CanView)]
public class DetailModel(IProjectService projects, ICurrentUser currentUser, IAuthorizationService authorization) : PageModel
{
    /// <summary>The prefix the New part form binds under (<c>NewPart.Sku</c>, ...).</summary>
    public const string NewPartPrefix = "NewPart";

    public ProjectDetail Project { get; private set; } = null!;

    public BomStatus Status { get; private set; } = null!;

    public IReadOnlyList<ProjectBuildItem> Builds { get; private set; } = [];

    /// <summary>Only used to hide edit actions; the handlers enforce <c>CanEdit</c> themselves.</summary>
    public bool CanEdit => currentUser.CanEdit;

    /// <summary>Only used to show "Change number"; its handlers enforce <c>RequireAdmin</c> themselves.</summary>
    public bool IsAdmin => currentUser.IsAdmin;

    /// <summary>
    /// A form shown on the page itself when its handler is opened without htmx (no JavaScript, or a plain link). With htmx the
    /// same form arrives as a dialog instead.
    /// </summary>
    public ProjectDialog Dialog { get; private set; }

    /// <summary>What the New part form shows after a failed post.</summary>
    public NewPartForm NewPart { get; private set; } = new();

    public string? NewNumber { get; private set; }

    /// <summary>The Build form as typed: units (text, so a mistake can be shown back) and note.</summary>
    public string BuildUnits { get; private set; } = "1";

    public string? BuildNote { get; private set; }

    /// <summary>The units the issue list is worked out for: <see cref="BuildUnits"/> when it is a valid number, else what remains.</summary>
    public int BuildUnitsValue { get; private set; } = 1;

    public async Task<IActionResult> OnGetAsync(int id)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        return Page();
    }

    // ---- BOM lines ---------------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostAddLineAsync(int id, int? productId, string? q, string? quantity, string? note)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var quantityValue = 0;
        if (!int.TryParse(quantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out quantityValue))
        {
            ModelState.AddModelError(ProjectFields.QuantityPerUnit, "Quantity per unit must be a whole number, 1 or more.");
        }

        if (productId is null)
        {
            ModelState.AddModelError(ProjectFields.ProductId, "Choose a part.");
        }

        if (ModelState.IsValid)
        {
            var result = await projects.AddLineAsync(id, new BomLineInput(productId!.Value, quantityValue, note), Actor, HttpContext.RequestAborted);
            if (result.Succeeded)
            {
                this.Flash($"Added {result.Value.Sku} to the BOM.");
                return await BomAsync(id);
            }

            ModelState.AddErrors(result);
        }

        return await BomAsync(id, productId, q, quantity, note);
    }

    public async Task<IActionResult> OnPostSaveLineAsync(int id, long lineId, string? quantity, string? note)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        if (!int.TryParse(quantity?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var quantityValue))
        {
            ModelState.AddModelError(LineKey(lineId), "Quantity per unit must be a whole number, 1 or more.");
        }
        else
        {
            var result = await projects.UpdateLineAsync(id, lineId, quantityValue, note, Actor, HttpContext.RequestAborted);
            if (!result.Succeeded)
            {
                ModelState.AddErrors(result, LineKey(lineId));
            }
        }

        return await BomAsync(id);
    }

    public async Task<IActionResult> OnPostRemoveLineAsync(int id, long lineId)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var result = await projects.RemoveLineAsync(id, lineId, Actor, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            this.Flash("Part removed from the BOM.");
        }
        else
        {
            ModelState.AddErrors(result, LineKey(lineId));
        }

        return await BomAsync(id);
    }

    public static string LineKey(long lineId) => $"line{lineId}";

    // ---- New part ----------------------------------------------------------------------------------------------

    public async Task<IActionResult> OnGetNewPartAsync(int id)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Dialog = ProjectDialog.NewPart;
        return this.IsHtmx() ? Partial("_NewPartModal", this) : Page();
    }

    public async Task<IActionResult> OnPostNewPartAsync(int id, [Bind(Prefix = NewPartPrefix)] NewPartForm input)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        NewPart = input;

        var quantity = 0;
        if (!int.TryParse(input.QuantityPerUnit?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out quantity))
        {
            ModelState.AddModelError($"{NewPartPrefix}.{ProjectFields.QuantityPerUnit}", "Quantity per unit must be a whole number, 1 or more.");
        }

        long cents = 0;
        if (!string.IsNullOrWhiteSpace(input.UnitCost) && !MoneyParser.TryParseCents(input.UnitCost, out cents))
        {
            ModelState.AddModelError($"{NewPartPrefix}.{ProjectFields.UnitCost}", "Enter an amount like 12.50 (at most two decimal places), or leave it blank.");
        }

        if (ModelState.IsValid)
        {
            var part = new NewPartInput(input.Sku, input.Name, string.IsNullOrWhiteSpace(input.Unit) ? null : input.Unit, cents);
            var result = await projects.CreatePartAndAddAsync(id, part, quantity, Actor, HttpContext.RequestAborted);
            if (result.Succeeded)
            {
                var message = $"Created {result.Value.Sku} and added it to the BOM.";
                if (!this.IsHtmx())
                {
                    TempData[PageExtensions.FlashKey] = message;
                    return RedirectToPage("Detail", new { id });
                }

                // The dialog's form asked for itself to be replaced; on success the BOM is swapped in instead and the dialog closes.
                this.Flash(message);
                Response.Headers["HX-Retarget"] = "#bom";
                Response.Headers["HX-Reswap"] = "outerHTML";
                Response.HxTrigger("closeModal");
                return await BomAsync(id);
            }

            ModelState.AddErrors(result, NewPartPrefix);
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Dialog = ProjectDialog.NewPart;
        if (!this.IsHtmx())
        {
            return Page();
        }

        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Partial("_NewPartForm", this);
    }

    // ---- Change number (Admin) ---------------------------------------------------------------------------------

    public async Task<IActionResult> OnGetChangeNumberAsync(int id)
    {
        if (!await IsAdminAsync())
        {
            return Forbid();
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Dialog = ProjectDialog.ChangeNumber;
        NewNumber = Project.Number;
        return this.IsHtmx() ? Partial("_ChangeNumberModal", this) : Page();
    }

    public async Task<IActionResult> OnPostChangeNumberAsync(int id, long rowVersion, string? newNumber)
    {
        if (!await IsAdminAsync())
        {
            return Forbid();
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        var oldNumber = Project.Number;
        var result = await projects.ChangeNumberAsync(id, rowVersion, newNumber, Actor, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            // The flash waits in TempData, so a dialog reloads the page behind it (the header shows the new number).
            TempData[PageExtensions.FlashKey] = $"Project number changed from {oldNumber} to {result.Value.Number}.";
            var url = Url.Page("Detail", new { id })!;
            return this.IsHtmx() ? this.HxRedirect(url) : Redirect(url);
        }

        ModelState.AddErrors(result);
        Dialog = ProjectDialog.ChangeNumber;
        NewNumber = newNumber;
        if (!this.IsHtmx())
        {
            return Page();
        }

        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Partial("_ChangeNumberForm", this);
    }

    // ---- Build -------------------------------------------------------------------------------------------------

    public async Task<IActionResult> OnGetBuildAsync(int id)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        Dialog = ProjectDialog.Build;
        SetBuildFields(null, null);
        return this.IsHtmx() ? Partial("_BuildModal", this) : Page();
    }

    /// <summary>The list of what a build of <paramref name="units"/> would issue (htmx, as the units change).</summary>
    public async Task<IActionResult> OnGetBuildPreviewAsync(int id, string? units)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        SetBuildFields(units, null);
        return Partial("_BuildList", this);
    }

    public async Task<IActionResult> OnPostBuildAsync(int id, string? units, string? note)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!int.TryParse(units?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unitCount))
        {
            ModelState.AddModelError(ProjectFields.Units, "Enter how many units to build as a whole number, 1 or more.");
        }
        else
        {
            var result = await projects.BuildAsync(id, unitCount, note, new StockContext(currentUser.Id!), HttpContext.RequestAborted);
            if (result.Succeeded)
            {
                var parts = result.Value.Movements.Sum(m => (long)Math.Abs(m.QuantityDelta));
                TempData[PageExtensions.FlashKey] =
                    $"Built {result.Value.Units} {(result.Value.Units == 1 ? "unit" : "units")}. Issued {parts:N0} {(parts == 1 ? "part" : "parts")} from stock.";
                return Back(Url.Page("Detail", new { id })!);
            }

            ModelState.AddErrors(result);

            // Stock may have moved while the dialog was open: the list below shows what is on hand now.
            await LoadAsync(id);
        }

        Dialog = ProjectDialog.Build;
        SetBuildFields(units, note);
        if (!this.IsHtmx())
        {
            return Page();
        }

        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        return Partial("_BuildForm", this);
    }

    private void SetBuildFields(string? units, string? note)
    {
        BuildUnits = units ?? Math.Max(1, Status.RemainingUnits).ToString(CultureInfo.InvariantCulture);
        BuildNote = note;
        BuildUnitsValue = int.TryParse(BuildUnits.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 1
            ? Math.Min(parsed, Data.Entities.Project.MaxTargetUnits)
            : Math.Max(1, Status.RemainingUnits);
    }

    // ---- Archive / delete --------------------------------------------------------------------------------------

    public async Task<IActionResult> OnPostArchiveAsync(int id, bool active, long rowVersion)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var existing = await projects.GetAsync(id, HttpContext.RequestAborted);
        if (existing is null)
        {
            return NotFound();
        }

        var result = await projects.SetActiveAsync(id, active, rowVersion, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            TempData[PageExtensions.FlashKey] = active ? $"Project '{existing.Name}' restored." : $"Project '{existing.Name}' archived.";
        }
        else
        {
            TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        }

        return Back(Url.Page("Detail", new { id })!);
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, long rowVersion)
    {
        if (!await this.CanEditAsync())
        {
            return Forbid();
        }

        var existing = await projects.GetAsync(id, HttpContext.RequestAborted);
        if (existing is null)
        {
            return Back(Url.Page("Index")!);
        }

        var result = await projects.DeleteAsync(id, rowVersion, HttpContext.RequestAborted);
        if (result.Succeeded)
        {
            TempData[PageExtensions.FlashKey] = $"Project '{existing.Name}' deleted.";
            return Back(Url.Page("Index")!);
        }

        TempData[PageExtensions.FlashErrorKey] = string.Join(" ", result.Errors.Select(e => e.Message));
        return Back(Url.Page("Detail", new { id })!);
    }

    // ---- helpers -----------------------------------------------------------------------------------------------

    private ProjectActor Actor => new(currentUser.Id!, currentUser.IsAdmin);

    private async Task<bool> IsAdminAsync() => (await authorization.AuthorizeAsync(User, Policies.RequireAdmin)).Succeeded;

    private IActionResult Back(string url) => this.IsHtmx() ? this.HxRedirect(url) : Redirect(url);

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
        Builds = await projects.GetBuildsAsync(id, HttpContext.RequestAborted);
        return true;
    }

    /// <summary>
    /// The BOM block for htmx (with the header chip and the totals riding along out of band); failures answer 422 so the swap
    /// shows the messages. Without htmx the messages ride TempData back to the page.
    /// </summary>
    private async Task<IActionResult> BomAsync(int id, int? productId = null, string? productLabel = null, string? quantity = null, string? note = null)
    {
        if (!await LoadAsync(id))
        {
            return NotFound();
        }

        if (!this.IsHtmx())
        {
            if (!ModelState.IsValid)
            {
                TempData[PageExtensions.FlashErrorKey] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            }

            return RedirectToPage("Detail", new { id });
        }

        if (!ModelState.IsValid && !Response.Headers.ContainsKey("HX-Retarget"))
        {
            Response.StatusCode = StatusCodes.Status422UnprocessableEntity;
        }

        return Partial("_Bom", new BomModel(Project, Status, CanEdit, ModelState, Oob: true, productId, productLabel, quantity, note));
    }

    public enum ProjectDialog
    {
        None,
        NewPart,
        ChangeNumber,
        Build,
    }

    /// <summary>The New part fields as typed. All text, so a mistake can be shown back in place; the service does the checking.</summary>
    public sealed class NewPartForm
    {
        public string? Sku { get; set; }

        public string? Name { get; set; }

        public string? Unit { get; set; } = "each";

        public string? UnitCost { get; set; }

        public string? QuantityPerUnit { get; set; } = "1";
    }
}

/// <summary>What the <c>_Bom</c> partial shows. <paramref name="Oob"/> adds the header and totals as out-of-band swaps.</summary>
public sealed record BomModel(
    ProjectDetail Project,
    BomStatus Status,
    bool CanEdit,
    ModelStateDictionary ModelState,
    bool Oob = false,
    int? ProductId = null,
    string? ProductLabel = null,
    string? Quantity = null,
    string? Note = null);
