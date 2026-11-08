using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Suppliers;

/// <summary>Creates a supplier (no id) or edits one. Page handlers only bind, call the service and render.</summary>
[Authorize(Policy = Policies.CanEdit)]
public class EditModel(ISupplierService suppliers) : PageModel
{
    [BindProperty]
    public SupplierForm Input { get; set; } = new();

    public SupplierDetail? Existing { get; private set; }

    public bool IsEdit => Existing is not null;

    /// <summary>True when the save failed because someone else changed the supplier first; the page offers a reload link.</summary>
    public bool Conflict { get; private set; }

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is not null)
        {
            Existing = await suppliers.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Existing is null)
            {
                return NotFound();
            }

            Input = SupplierForm.From(Existing);
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        if (id is not null)
        {
            Existing = await suppliers.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Existing is null)
            {
                return NotFound();
            }
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var input = new SupplierInput(Input.Name, Input.ContactName, Input.Email, Input.Phone, Input.Address, Input.Website, Input.Notes);
        var result = Existing is null
            ? await suppliers.CreateAsync(input, HttpContext.RequestAborted)
            : await suppliers.UpdateAsync(Existing.Id, Input.RowVersion, input, HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            Conflict = result.Errors.Any(e => e.Code == "supplier.conflict");
            ModelState.AddErrors(result, nameof(Input));
            return Page();
        }

        this.Flash(Existing is null ? $"Supplier '{result.Value.Name}' created." : $"Supplier '{result.Value.Name}' saved.");
        return RedirectToPage("Detail", new { id = result.Value.Id });
    }

    public sealed class SupplierForm
    {
        [Required(ErrorMessage = "Name is required."), StringLength(Supplier.NameMaxLength, ErrorMessage = "Name can be at most 200 characters.")]
        public string? Name { get; set; }

        [Display(Name = "Contact name"), StringLength(Supplier.ContactNameMaxLength, ErrorMessage = "Contact name can be at most 100 characters.")]
        public string? ContactName { get; set; }

        [StringLength(Supplier.EmailMaxLength, ErrorMessage = "Email can be at most 200 characters.")]
        public string? Email { get; set; }

        [StringLength(Supplier.PhoneMaxLength, ErrorMessage = "Phone can be at most 50 characters.")]
        public string? Phone { get; set; }

        [StringLength(Supplier.AddressMaxLength, ErrorMessage = "Address can be at most 500 characters.")]
        public string? Address { get; set; }

        [StringLength(Supplier.WebsiteMaxLength, ErrorMessage = "Website can be at most 200 characters.")]
        public string? Website { get; set; }

        [StringLength(Supplier.NotesMaxLength, ErrorMessage = "Notes can be at most 2000 characters.")]
        public string? Notes { get; set; }

        /// <summary>The version the form was loaded with; a stale one is reported as an edit conflict.</summary>
        public long RowVersion { get; set; }

        public static SupplierForm From(SupplierDetail s) => new()
        {
            Name = s.Name,
            ContactName = s.ContactName,
            Email = s.Email,
            Phone = s.Phone,
            Address = s.Address,
            Website = s.Website,
            Notes = s.Notes,
            RowVersion = s.RowVersion,
        };
    }
}
