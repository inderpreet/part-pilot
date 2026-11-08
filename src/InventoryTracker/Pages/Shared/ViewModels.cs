using InventoryTracker.Services;

namespace InventoryTracker.Pages.Shared;

/// <summary>
/// Model for the <c>_Pager</c> partial. Links keep the current query string and replace only <c>page</c>.
/// For a list that is a fragment of a bigger page (<paramref name="Handler"/> = the page handler that renders just the
/// fragment, e.g. a tab) the <c>href</c> stays the full-page URL (works without JavaScript) while htmx fetches the
/// fragment and does not touch the address bar.
/// </summary>
public sealed record PagerModel(int Page, int PageSize, int TotalCount, string Target = "#results", string? Handler = null)
{
    public int TotalPages => PageSize <= 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);

    public static PagerModel From<T>(PagedResult<T> result, string target = "#results", string? handler = null)
        => new(result.Page, result.PageSize, result.TotalCount, target, handler);
}

/// <summary>Model for the <c>_MovementTable</c> partial (a product's history tab and the global movements list).</summary>
/// <param name="ReturnUrl">Where a Reverse button sends a no-JavaScript browser back to (a local URL).</param>
public sealed record MovementTableModel(
    PagedResult<StockMovementItem> Page,
    bool ShowProduct,
    bool CanEdit,
    string ReturnUrl,
    string PagerTarget = "#results",
    string? PagerHandler = null,
    string? EmptyTitle = null,
    string? EmptyMessage = null);

/// <summary>Model for the <c>_SortHeader</c> partial: a <c>&lt;th&gt;</c> that toggles sorting by <paramref name="Column"/>.</summary>
public sealed record SortHeaderModel(string Column, string Label, ListQuery Query, string Target = "#results");

/// <summary>Model for the <c>_EmptyState</c> partial.</summary>
public sealed record EmptyStateModel(string Title, string? Message = null, string? ActionText = null, string? ActionUrl = null);

/// <summary>
/// Model for the <c>_ProductPicker</c> partial: a search-as-you-type product chooser that posts the chosen product id
/// as <paramref name="FieldName"/> (e.g. <c>Input.ProductId</c>). <paramref name="SelectedId"/>/<paramref name="SelectedLabel"/>
/// pre-fill it when editing.
/// </summary>
public sealed record ProductPickerModel(
    string FieldName,
    int? SelectedId = null,
    string? SelectedLabel = null,
    bool ActiveOnly = true,
    string Label = "Product",
    bool Required = true,
    string LookupUrl = "/Products/Lookup")
{
    /// <summary>Unique-per-field id fragment so several pickers can share a page.</summary>
    public string DomId => FieldName.Replace('.', '-').Replace('[', '-').Replace(']', '-').ToLowerInvariant();
}

/// <summary>Model for the <c>_ProductPickerResults</c> partial (the dropdown rows).</summary>
public sealed record ProductPickerResultsModel(IReadOnlyList<ProductLookupItem> Items, string? Query)
{
    /// <summary>True when the typed text is exactly this item's SKU or barcode, i.e. a scanned code.</summary>
    public bool IsExact(ProductLookupItem item)
        => !string.IsNullOrEmpty(Query)
           && (string.Equals(item.Sku, Query, StringComparison.OrdinalIgnoreCase)
               || (item.Barcode is not null && string.Equals(item.Barcode, Query, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>Model for the <c>_ConfirmButton</c> partial: a button that asks for confirmation, then <c>hx-post</c>s.</summary>
public sealed record ConfirmButtonModel(
    string Text,
    string Confirm,
    string PostUrl,
    string? Target = null,
    string? Swap = null,
    string CssClass = "secondary");

/// <summary>Model for the <c>_MoveButtons</c> partial: the "+ In / âˆ’ Out / Adjust" buttons that open the quick stock-movement dialog.</summary>
public sealed record MoveButtonsModel(int ProductId, string Sku);
/// <summary>Which side of the product–supplier relationship a links table is shown from: a supplier's products, or a product's suppliers.</summary>
public enum LinkView
{
    Supplier,
    Product,
}

/// <summary>Values of a link form (the "add" form, or an inline "edit" row); text fields stay as typed so errors can be shown next to them.</summary>
public sealed record SupplierLinkFormModel(
    int? ProductId,
    int? SupplierId,
    string? ProductLabel,
    string? SupplierSku,
    string? UnitCost,
    string? LeadTimeDays,
    string? MinOrderQuantity,
    bool IsPreferred);

/// <summary>
/// Model for <c>_SupplierLinks</c>: the table of links plus its "add" form. Used on the supplier page (<see cref="LinkView.Supplier"/>,
/// <see cref="OwnerId"/> = supplier id) and the product page (<see cref="LinkView.Product"/>, <see cref="OwnerId"/> = product id).
/// </summary>
/// <param name="Candidates">Product view only: the suppliers that can still be added.</param>
/// <param name="OwnerActive">False for an archived supplier / product: existing links can be edited, new ones cannot be added.</param>
/// <param name="Add">What was typed in the "add" form when it came back with errors.</param>
public sealed record SupplierLinksModel(
    LinkView View,
    int OwnerId,
    IReadOnlyList<ProductSupplierItem> Items,
    bool CanEdit,
    bool OwnerActive,
    IReadOnlyList<SupplierLookupItem> Candidates,
    SupplierLinkFormModel? Add = null)
{
    /// <summary>The element id of the whole block; every link action swaps this element.</summary>
    public string SectionId => SectionIdFor(View);

    public static string SectionIdFor(LinkView view) => view == LinkView.Supplier ? "supplier-links" : "product-suppliers";
}

/// <summary>Model for <c>_SupplierLinkRow</c>.</summary>
public sealed record SupplierLinkRowModel(LinkView View, ProductSupplierItem Item, bool CanEdit)
{
    public string SectionId => SupplierLinksModel.SectionIdFor(View);

    public string RowId => $"link-{Item.ProductId}-{Item.SupplierId}";
}

/// <summary>Model for <c>_SupplierLinkEditRow</c>: an inline edit form in place of a row.</summary>
public sealed record SupplierLinkEditRowModel(LinkView View, ProductSupplierItem Item, SupplierLinkFormModel Form)
{
    public string RowId => $"link-{Item.ProductId}-{Item.SupplierId}";
}
