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

    public async Task OnGetAsync()
    {
        var current = await settings.GetBusinessSettingsAsync(HttpContext.RequestAborted);
        Input = new SettingsInput
        {
            CompanyName = current.CompanyName,
            CurrencyCode = current.CurrencyCode,
            CurrencySymbol = current.CurrencySymbol,
            AllowNegativeStock = current.AllowNegativeStock,
            PurchaseOrderNumberPrefix = current.PurchaseOrderNumberPrefix,
        };
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await settings.UpdateBusinessSettingsAsync(
            new BusinessSettings(Input.CompanyName, Input.CurrencyCode, Input.CurrencySymbol, Input.AllowNegativeStock, Input.PurchaseOrderNumberPrefix ?? string.Empty),
            HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            ModelState.AddErrors(result, nameof(Input));
            return Page();
        }

        this.Flash("Settings saved.");
        return RedirectToPage();
    }

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
    }
}
