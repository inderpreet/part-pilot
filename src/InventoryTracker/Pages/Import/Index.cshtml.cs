using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Import;

[Authorize(Policy = Policies.CanImport)]
public class IndexModel(
    IImportService imports,
    IAuthorizationService authorization,
    ICurrentUser currentUser) : PageModel
{
    [BindProperty]
    public ImportKind Kind { get; set; } = ImportKind.Products;

    [BindProperty]
    public BomImportMode Mode { get; set; } = BomImportMode.Merge;

    [BindProperty]
    public bool CreateMissingParts { get; set; }

    [BindProperty]
    public bool SkipInvalid { get; set; }

    [BindProperty]
    public string? Token { get; set; }

    public IReadOnlyList<ImportKind> AllowedKinds { get; private set; } = [];

    public ImportPreview? Preview { get; private set; }

    public ImportBatchResult? Summary { get; private set; }

    public PagedResult<ImportBatchResult> History { get; private set; } = new([], 1, ListQuery.DefaultPageSize, 0);

    public async Task<IActionResult> OnGetAsync(long? batch)
    {
        await LoadAsync();
        if (!AllowedKinds.Contains(Kind))
        {
            Kind = AllowedKinds.Count > 0 ? AllowedKinds[0] : ImportKind.Products;
        }

        if (batch is { } id)
        {
            Summary = await imports.GetBatchAsync(id, HttpContext.RequestAborted);
            if (Summary is null)
            {
                return NotFound();
            }
        }

        return Page();
    }

    public async Task<IActionResult> OnGetTemplateAsync(ImportKind kind)
    {
        if (!await MayAsync(kind))
        {
            return Forbid();
        }

        return new CsvResult(ImportKinds.TemplateFileName(kind), async (stream, ct) =>
        {
            await using var source = imports.OpenTemplate(kind);
            await source.CopyToAsync(stream, ct);
        });
    }

    public async Task<IActionResult> OnPostUploadAsync(IFormFile? file)
    {
        await LoadAsync();
        if (!await MayAsync(Kind))
        {
            return Forbid();
        }

        if (file is null || file.Length == 0)
        {
            ModelState.AddModelError("file", "Choose a CSV file.");
            return Page();
        }

        await using var stream = file.OpenReadStream();
        var result = await imports.PreviewAsync(
            Kind, stream, file.FileName, new ImportOptions(Mode, CreateMissingParts), Actor(), HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ModelState.AddErrors(result);
            return Page();
        }

        Preview = result.Value;
        Token = result.Value.Token;
        Kind = result.Value.Kind;
        Mode = result.Value.Options.Mode;
        CreateMissingParts = result.Value.Options.CreateMissingParts;
        return Page();
    }

    public async Task<IActionResult> OnPostCommitAsync()
    {
        await LoadAsync();
        if (string.IsNullOrEmpty(Token))
        {
            ModelState.AddModelError(string.Empty, "Upload a file first.");
            return Page();
        }

        // The page policy passes on either import permission, and a permission can be revoked between preview and commit.
        if (imports.StagedKind(Token, currentUser.Id!) is { } kind && !await MayAsync(kind))
        {
            return Forbid();
        }

        var result = await imports.CommitAsync(Token, Actor(), SkipInvalid, HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            ModelState.AddErrors(result);
            Preview = await imports.PeekAsync(Token, Actor(), HttpContext.RequestAborted);
            return Page();
        }

        var url = Url.Page("/Import/Index", new { batch = result.Value.Id })!;
        return this.IsHtmx() ? this.HxRedirect(url) : Redirect(url);
    }

    public async Task<IActionResult> OnPostCancelAsync()
    {
        if (!string.IsNullOrEmpty(Token) && currentUser.Id is { } userId)
        {
            await imports.CancelAsync(Token, userId, HttpContext.RequestAborted);
        }

        this.Flash("Import cancelled.");
        return RedirectToPage();
    }

    private ImportActor Actor() => new(currentUser.Id!, currentUser.IsAdmin);

    private async Task LoadAsync()
    {
        var kinds = new List<ImportKind>();
        if ((await authorization.AuthorizeAsync(User, Policies.CanImportData)).Succeeded)
        {
            kinds.AddRange([ImportKind.Products, ImportKind.Suppliers, ImportKind.ProductSuppliers, ImportKind.OpeningStock]);
        }

        if ((await authorization.AuthorizeAsync(User, Policies.CanImportBoms)).Succeeded)
        {
            kinds.Add(ImportKind.Boms);
        }

        AllowedKinds = kinds;
        History = await imports.SearchBatchesAsync(new ListQuery { PageSize = 20 }, HttpContext.RequestAborted);
    }

    private async Task<bool> MayAsync(ImportKind kind)
        => (await authorization.AuthorizeAsync(User, kind == ImportKind.Boms ? Policies.CanImportBoms : Policies.CanImportData)).Succeeded;
}
