using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Admin;

[Authorize(Policy = Policies.RequireAdmin)]
public class SettingsModel(ISettingsService settings) : PageModel
{
    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    [BindProperty]
    public PermissionsInput Permissions { get; set; } = new();

    public async Task OnGetAsync() => await LoadAsync();

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            var perms = await settings.GetDataPermissionsAsync(HttpContext.RequestAborted);
            Permissions = From(perms);
            return Page();
        }

        var result = await settings.UpdateBusinessSettingsAsync(
            new BusinessSettings(Input.CompanyName, Input.CurrencyCode, Input.CurrencySymbol, Input.AllowNegativeStock, Input.PurchaseOrderNumberPrefix ?? string.Empty, Input.ProjectNumberPrefix ?? string.Empty, Input.RequisitionNumberPrefix ?? string.Empty),
            HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ModelState.AddErrors(result, nameof(Input));
            return Page();
        }

        this.Flash("Settings saved.");
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPermissionsAsync()
    {
        var result = await settings.UpdateDataPermissionsAsync(
            new DataPermissionSettings(
                Permissions.StaffImportData, Permissions.ViewerImportData,
                Permissions.StaffImportBoms, Permissions.ViewerImportBoms,
                Permissions.StaffExportData, Permissions.ViewerExportData,
                Permissions.StaffExportBoms, Permissions.ViewerExportBoms),
            HttpContext.RequestAborted);
        if (!result.Succeeded)
        {
            await LoadAsync();
            ModelState.AddErrors(result);
            return Page();
        }

        this.Flash("Data permissions saved.");
        return RedirectToPage();
    }

    private async Task LoadAsync()
    {
        var current = await settings.GetBusinessSettingsAsync(HttpContext.RequestAborted);
        Input = new SettingsInput
        {
            CompanyName = current.CompanyName,
            CurrencyCode = current.CurrencyCode,
            CurrencySymbol = current.CurrencySymbol,
            AllowNegativeStock = current.AllowNegativeStock,
            PurchaseOrderNumberPrefix = current.PurchaseOrderNumberPrefix,
            ProjectNumberPrefix = current.ProjectNumberPrefix,
            RequisitionNumberPrefix = current.RequisitionNumberPrefix,
        };
        Permissions = From(await settings.GetDataPermissionsAsync(HttpContext.RequestAborted));
    }

    private static PermissionsInput From(DataPermissionSettings perms) => new()
    {
        StaffImportData = perms.StaffImportData,
        ViewerImportData = perms.ViewerImportData,
        StaffImportBoms = perms.StaffImportBoms,
        ViewerImportBoms = perms.ViewerImportBoms,
        StaffExportData = perms.StaffExportData,
        ViewerExportData = perms.ViewerExportData,
        StaffExportBoms = perms.StaffExportBoms,
        ViewerExportBoms = perms.ViewerExportBoms,
    };

    public sealed class SettingsInput
    {
        [Required(ErrorMessage = "Company name is required."), StringLength(BusinessSettings.CompanyNameMaxLength)]
        [Display(Name = "Company name")]
        public string CompanyName { get; set; } = string.Empty;

        [Required(ErrorMessage = "Currency code is required."), StringLength(3, MinimumLength = 3, ErrorMessage = "Currency code must be exactly 3 letters, e.g. USD.")]
        [Display(Name = "Currency code")]
        public string CurrencyCode { get; set; } = string.Empty;

        [Required(ErrorMessage = "Currency symbol is required."), StringLength(BusinessSettings.CurrencySymbolMaxLength)]
        [Display(Name = "Currency symbol")]
        public string CurrencySymbol { get; set; } = string.Empty;

        [Display(Name = "Allow negative stock")]
        public bool AllowNegativeStock { get; set; }

        [StringLength(BusinessSettings.PurchaseOrderPrefixMaxLength)]
        [Display(Name = "PO number prefix")]
        public string? PurchaseOrderNumberPrefix { get; set; }

        [StringLength(BusinessSettings.ProjectPrefixMaxLength)]
        [Display(Name = "BOM number prefix")]
        public string? ProjectNumberPrefix { get; set; }

        [StringLength(BusinessSettings.RequisitionPrefixMaxLength)]
        [Display(Name = "Requisition number prefix")]
        public string? RequisitionNumberPrefix { get; set; }
    }

    public sealed class PermissionsInput
    {
        public bool StaffImportData { get; set; }
        public bool ViewerImportData { get; set; }
        public bool StaffImportBoms { get; set; }
        public bool ViewerImportBoms { get; set; }
        public bool StaffExportData { get; set; }
        public bool ViewerExportData { get; set; }
        public bool StaffExportBoms { get; set; }
        public bool ViewerExportBoms { get; set; }
    }
}
