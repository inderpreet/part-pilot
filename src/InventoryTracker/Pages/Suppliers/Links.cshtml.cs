using System.Globalization;
using InventoryTracker.Auth;
using InventoryTracker.Data.Entities;
using InventoryTracker.Hosting;
using InventoryTracker.Pages.Shared;
using InventoryTracker.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace InventoryTracker.Pages.Suppliers;

/// <summary>
/// The one endpoint behind every product–supplier link action, for both places the links are shown: the supplier page
/// (<c>view=Supplier</c>: the supplier's products) and the product page (<c>view=Product</c>: the product's suppliers).
/// Add, edit, remove and the preferred star all answer with the whole links block, so the two sides never disagree and
/// switching the preferred supplier visibly clears the previous one. Without htmx a post redirects back to the owning page.
/// </summary>
[Authorize(Policy = Policies.CanEdit)]
public class LinksModel(ISupplierService suppliers, IProductService products) : PageModel
{
    public IActionResult OnGet() => RedirectToPage("/Suppliers/Index");

    /// <summary>The row switched into edit mode.</summary>
    public async Task<IActionResult> OnGetEditAsync(LinkView view, int productId, int supplierId)
    {
        var item = await suppliers.GetLinkAsync(productId, supplierId, HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        return this.IsHtmx()
            ? Partial("_SupplierLinkEditRow", new SupplierLinkEditRowModel(view, item, FormFor(item)))
            : Redirect(OwnerUrl(view, productId, supplierId));
    }

    /// <summary>The plain row again (Cancel on an inline edit).</summary>
    public async Task<IActionResult> OnGetRowAsync(LinkView view, int productId, int supplierId)
    {
        var item = await suppliers.GetLinkAsync(productId, supplierId, HttpContext.RequestAborted);
        if (item is null)
        {
            return NotFound();
        }

        return this.IsHtmx()
            ? Partial("_SupplierLinkRow", new SupplierLinkRowModel(view, item, CanEdit: true))
            : Redirect(OwnerUrl(view, productId, supplierId));
    }

    /// <summary>
    /// Adds a link (<c>mode=add</c>) or saves the terms of an existing one (<c>mode=edit</c>). Failures come back as 422:
    /// an edit swaps its own row, the add form swaps the whole block with what was typed and the errors.
    /// </summary>
    public async Task<IActionResult> OnPostSaveAsync(
        string? mode, LinkView view, int? productId, int? supplierId,
        string? supplierSku, string? unitCost, string? leadTimeDays, string? minOrderQuantity, bool isPreferred)
    {
        var editing = string.Equals(mode, "edit", StringComparison.Ordinal);
        var form = new SupplierLinkFormModel(productId, supplierId, null, supplierSku, unitCost, leadTimeDays, minOrderQuantity, isPreferred);

        long cents = 0;
        if (!string.IsNullOrWhiteSpace(unitCost) && !MoneyParser.TryParseCents(unitCost, out cents))
        {
            ModelState.AddModelError(SupplierLinkFields.UnitCost, "Enter an amount like 12.50 (at most two decimal places).");
        }

        int? lead = null;
        if (!string.IsNullOrWhiteSpace(leadTimeDays))
        {
            if (int.TryParse(leadTimeDays.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var days))
            {
                lead = days;
            }
            else
            {
                ModelState.AddModelError(SupplierLinkFields.LeadTimeDays, "Lead time must be a whole number of days.");
            }
        }

        var minimum = 1;
        if (!string.IsNullOrWhiteSpace(minOrderQuantity) && !int.TryParse(minOrderQuantity.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out minimum))
        {
            ModelState.AddModelError(SupplierLinkFields.MinOrderQuantity, "Minimum order must be a whole number, 1 or more.");
        }

        if (productId is null)
        {
            ModelState.AddModelError(SupplierLinkFields.ProductId, "Choose a part.");
        }

        if (supplierId is null)
        {
            ModelState.AddModelError(SupplierLinkFields.SupplierId, "Choose a supplier.");
        }

        if (ModelState.IsValid)
        {
            var result = await suppliers.LinkProductAsync(
                productId!.Value, supplierId!.Value, new SupplierLinkInput(supplierSku, cents, lead, minimum, isPreferred), HttpContext.RequestAborted);

            if (result.Succeeded)
            {
                var item = result.Value;
                this.Flash((editing ? $"Saved the terms of {item.Sku} from {item.SupplierName}." : $"Linked {item.Sku} to {item.SupplierName}.")
                           + (item.IsPreferred ? " It is now the preferred supplier." : string.Empty));

                return await BlockAsync(view, item.ProductId, item.SupplierId, retarget: true);
            }

            ModelState.AddErrors(result);
        }

        return await InvalidAsync(editing, view, form);
    }

    /// <summary>Removes a link. Existing purchase orders keep their own copy of what they need.</summary>
    public async Task<IActionResult> OnPostRemoveAsync(LinkView view, int productId, int supplierId)
    {
        var item = await suppliers.GetLinkAsync(productId, supplierId, HttpContext.RequestAborted);
        await suppliers.UnlinkProductAsync(productId, supplierId, HttpContext.RequestAborted);

        if (item is not null)
        {
            this.Flash($"Removed {item.SupplierName} as a supplier of {item.Sku}.");
        }

        return await BlockAsync(view, productId, supplierId);
    }

    /// <summary>The preferred star: <c>preferred=true</c> makes this the product's preferred supplier (clearing the previous one), false clears it.</summary>
    public async Task<IActionResult> OnPostPreferredAsync(LinkView view, int productId, int supplierId, bool preferred)
    {
        var item = await suppliers.GetLinkAsync(productId, supplierId, HttpContext.RequestAborted);
        var result = await suppliers.SetPreferredAsync(productId, supplierId, preferred, HttpContext.RequestAborted);

        if (!result.Succeeded)
        {
            this.FlashError(string.Join(" ", result.Errors.Select(e => e.Message)));
        }
        else if (item is not null)
        {
            this.Flash(preferred
                ? $"{item.SupplierName} is now the preferred supplier of {item.Sku}."
                : $"{item.SupplierName} is no longer the preferred supplier of {item.Sku}.");
        }

        return await BlockAsync(view, productId, supplierId);
    }

    private static SupplierLinkFormModel FormFor(ProductSupplierItem item) => new(
        item.ProductId,
        item.SupplierId,
        null,
        item.SupplierSku,
        MoneyParser.ToPlainString(item.UnitCostCents),
        item.LeadTimeDays?.ToString(CultureInfo.InvariantCulture),
        item.MinOrderQuantity.ToString(CultureInfo.InvariantCulture),
        item.IsPreferred);

    private string OwnerUrl(LinkView view, int productId, int supplierId)
        => view == LinkView.Supplier
            ? Url.Page("/Suppliers/Detail", new { id = supplierId })!
            : Url.Page("/Products/Detail", new { id = productId })!;

    /// <summary>The links block of the page the action came from (htmx), or that page itself (plain post).</summary>
    private async Task<IActionResult> BlockAsync(LinkView view, int productId, int supplierId, SupplierLinkFormModel? add = null, bool retarget = false)
    {
        if (!this.IsHtmx())
        {
            return Redirect(OwnerUrl(view, productId, supplierId));
        }

        var block = await SupplierLinksBuilder.BuildAsync(
            suppliers, products, view, view == LinkView.Supplier ? supplierId : productId, canEdit: true, add, HttpContext.RequestAborted);
        if (block is null)
        {
            return NotFound();
        }

        if (retarget)
        {
            // An inline edit posts into its own row; once it succeeded the whole block is what changed (the star may have moved).
            Response.Headers["HX-Retarget"] = "#" + block.SectionId;
            Response.Headers["HX-Reswap"] = "outerHTML";
        }

        return Partial("_SupplierLinks", block);
    }

    private async Task<IActionResult> InvalidAsync(bool editing, LinkView view, SupplierLinkFormModel form)
    {
        if (!this.IsHtmx())
        {
            this.FlashError(string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage)));
            return Redirect(OwnerUrl(view, form.ProductId ?? 0, form.SupplierId ?? 0));
        }

        Response.StatusCode = StatusCodes.Status422UnprocessableEntity;

        if (editing)
        {
            var item = form.ProductId is { } p && form.SupplierId is { } s ? await suppliers.GetLinkAsync(p, s, HttpContext.RequestAborted) : null;
            return item is null ? NotFound() : Partial("_SupplierLinkEditRow", new SupplierLinkEditRowModel(view, item, form));
        }

        // Put the chosen product back into the picker.
        if (form.ProductId is { } productId && await products.GetAsync(productId, HttpContext.RequestAborted) is { } product)
        {
            form = form with { ProductLabel = $"{product.Sku} — {product.Name}" };
        }

        var ownerId = view == LinkView.Supplier ? form.SupplierId : form.ProductId;
        var block = ownerId is { } id
            ? await SupplierLinksBuilder.BuildAsync(suppliers, products, view, id, canEdit: true, form, HttpContext.RequestAborted)
            : null;
        return block is null ? NotFound() : Partial("_SupplierLinks", block);
    }
}
