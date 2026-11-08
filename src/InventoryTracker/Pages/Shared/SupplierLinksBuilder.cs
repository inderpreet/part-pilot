using InventoryTracker.Services;

namespace InventoryTracker.Pages.Shared;

/// <summary>Builds the <see cref="SupplierLinksModel"/> shown on the supplier page, the product page and after every link action.</summary>
public static class SupplierLinksBuilder
{
    /// <summary>The links block for a supplier (<paramref name="view"/> = Supplier) or a product (Product); <c>null</c> when that supplier / product does not exist.</summary>
    public static async Task<SupplierLinksModel?> BuildAsync(
        ISupplierService suppliers,
        IProductService products,
        LinkView view,
        int ownerId,
        bool canEdit,
        SupplierLinkFormModel? add = null,
        CancellationToken cancellationToken = default)
    {
        if (view == LinkView.Supplier)
        {
            var supplier = await suppliers.GetAsync(ownerId, cancellationToken);
            return supplier is null
                ? null
                : new SupplierLinksModel(view, ownerId, await suppliers.GetProductsAsync(ownerId, cancellationToken), canEdit, supplier.IsActive, [], add);
        }

        var product = await products.GetAsync(ownerId, cancellationToken);
        if (product is null)
        {
            return null;
        }

        var items = await suppliers.GetForProductAsync(ownerId, cancellationToken);
        var candidates = canEdit && product.IsActive ? await suppliers.ListUnlinkedForProductAsync(ownerId, cancellationToken: cancellationToken) : [];
        return new SupplierLinksModel(view, ownerId, items, canEdit, product.IsActive, candidates, add);
    }
}
