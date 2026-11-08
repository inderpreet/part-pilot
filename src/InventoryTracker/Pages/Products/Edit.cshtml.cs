using System.ComponentModel.DataAnnotations;
using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Products;

/// <summary>Creates a product (no id) or edits one. Page handlers only bind, call the service and render.</summary>
[Authorize(Policy = Policies.CanEdit)]
public class EditModel(IProductService products, ICategoryService categories) : PageModel
{
    [BindProperty]
    public ProductForm Input { get; set; } = new();

    public ProductDetail? Existing { get; private set; }

    public bool IsEdit => Existing is not null;

    /// <summary>True when the save failed because someone else changed the product first; the page offers a reload link.</summary>
    public bool Conflict { get; private set; }

    /// <summary>Categories for the select: all active ones, plus the product's current one even if archived.</summary>
    public IReadOnlyList<CategoryListItem> CategoryOptions { get; private set; } = [];

    public async Task<IActionResult> OnGetAsync(int? id)
    {
        if (id is not null)
        {
            Existing = await products.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Existing is null)
            {
                return NotFound();
            }

            Input = ProductForm.From(Existing);
        }

        await LoadCategoriesAsync();
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int? id)
    {
        if (id is not null)
        {
            Existing = await products.GetAsync(id.Value, HttpContext.RequestAborted);
            if (Existing is null)
            {
                return NotFound();
            }
        }

        // Money is typed text; blank means 0.00.
        long costCents = 0;
        if (!string.IsNullOrWhiteSpace(Input.UnitCost))
        {
            if (!MoneyParser.TryParseCents(Input.UnitCost, out costCents))
            {
                ModelState.AddModelError($"{nameof(Input)}.{nameof(Input.UnitCost)}", "Enter an amount like 12.50 (at most two decimal places).");
            }
        }

        if (!ModelState.IsValid)
        {
            await LoadCategoriesAsync();
            return Page();
        }

        var input = new ProductInput(
            Input.Sku, Input.Name, Input.Description, Input.CategoryId, Input.Unit, Input.Barcode, Input.BinLocation,
            Input.ReorderPoint ?? 0, Input.ReorderQuantity ?? 0, costCents);

        var result = Existing is null
            ? await products.CreateAsync(input, HttpContext.RequestAborted)
            : await products.UpdateAsync(Existing.Id, Input.RowVersion, input, HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            Conflict = result.Errors.Any(e => e.Code == "product.conflict");
            ModelState.AddErrors(result, nameof(Input));
            await LoadCategoriesAsync();
            return Page();
        }

        this.Flash(Existing is null ? $"Part '{result.Value.Sku}' created." : $"Part '{result.Value.Sku}' saved.");
        return RedirectToPage("Detail", new { id = result.Value.Id });
    }

    private async Task LoadCategoriesAsync()
    {
        var all = await categories.ListAsync(includeArchived: true, HttpContext.RequestAborted);
        var keep = Existing?.CategoryId ?? Input.CategoryId;
        CategoryOptions = [.. all.Where(c => c.IsActive || c.Id == keep)];
    }

    public sealed class ProductForm
    {
        [Required(ErrorMessage = "SKU is required."), StringLength(Product.SkuMaxLength, ErrorMessage = "SKU can be at most 40 characters.")]
        public string? Sku { get; set; }

        [Required(ErrorMessage = "Name is required."), StringLength(Product.NameMaxLength, ErrorMessage = "Name can be at most 200 characters.")]
        public string? Name { get; set; }

        [StringLength(Product.DescriptionMaxLength, ErrorMessage = "Description can be at most 2000 characters.")]
        public string? Description { get; set; }

        public int? CategoryId { get; set; }

        [StringLength(Product.UnitMaxLength, ErrorMessage = "Unit can be at most 20 characters.")]
        public string? Unit { get; set; } = Product.DefaultUnit;

        [StringLength(Product.BarcodeMaxLength, ErrorMessage = "Barcode can be at most 64 characters.")]
        public string? Barcode { get; set; }

        [Display(Name = "Bin location"), StringLength(Product.BinLocationMaxLength, ErrorMessage = "Bin location can be at most 50 characters.")]
        public string? BinLocation { get; set; }

        [Display(Name = "Reorder point"), Range(0, int.MaxValue, ErrorMessage = "Reorder point must be a whole number, 0 or more.")]
        public int? ReorderPoint { get; set; }

        [Display(Name = "Reorder quantity"), Range(0, int.MaxValue, ErrorMessage = "Reorder quantity must be a whole number, 0 or more.")]
        public int? ReorderQuantity { get; set; }

        /// <summary>Typed amount ("12.50"); converted to cents by <see cref="MoneyParser"/>.</summary>
        [Display(Name = "Unit cost")]
        public string? UnitCost { get; set; }

        /// <summary>The version the form was loaded with; a stale one is reported as an edit conflict.</summary>
        public long RowVersion { get; set; }

        public static ProductForm From(ProductDetail p) => new()
        {
            Sku = p.Sku,
            Name = p.Name,
            Description = p.Description,
            CategoryId = p.CategoryId,
            Unit = p.Unit,
            Barcode = p.Barcode,
            BinLocation = p.BinLocation,
            ReorderPoint = p.ReorderPoint,
            ReorderQuantity = p.ReorderQuantity,
            UnitCost = MoneyParser.ToPlainString(p.UnitCostCents),
            RowVersion = p.RowVersion,
        };
    }
}
