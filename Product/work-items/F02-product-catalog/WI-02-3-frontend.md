# WI-02-3 — Product list/detail/edit, categories

- **Feature:** [F02 — Product catalog](../../Features/F02-product-catalog.md)
- **Status:** Planned
- **Depends on:** WI-02-2, WI-00-3
- **Estimate:** M

## Goal

Users can browse, search, create, edit and archive products and categories.

## Scope

- In: `/Products`, `/Products/{id}`, `/Products/Edit`, `/Categories`, product picker partial.
- Out: stock actions on detail page (WI-03-3), supplier tab (WI-04-3).

## Tasks

- [ ] `/Products`: search box (live, 300 ms debounce, autofocus for scanner input), category + status filters, sortable table,
      pager, low-stock badge; Enter on exact SKU/barcode match navigates to detail.
- [ ] `_ProductRows` partial for HTMX swaps; `hx-push-url` keeps filters in URL.
- [ ] `/Products/{id}`: header (SKU, name, status), fields card, on-hand big number, tab area placeholders (History, Suppliers, POs, Audit).
- [ ] `/Products/Edit/{id?}`: form with validation, category select, money input, SKU read-only when locked; concurrency error message with reload link.
- [ ] Archive/restore button with `hx-confirm`.
- [ ] `_ProductPicker` partial: input + dropdown results (`/Products/Lookup`), sets hidden ProductId — reused by F03/F05.
- [ ] `/Categories`: list with inline create/edit (HTMX row swap), archive toggle.
- [ ] Hide edit actions for Viewer.
- [ ] Page tests: list renders, HTMX partial returned on `HX-Request`, Viewer POST returns 403.

## Acceptance criteria

- [ ] Create → appears in list → edit → archive → hidden under "Active" filter, visible under "Archived".
- [ ] Scanner-style input (type SKU + Enter) opens product detail.
- [ ] Works at tablet width.

## Notes

—
