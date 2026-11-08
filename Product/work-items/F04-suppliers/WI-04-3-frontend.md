# WI-04-3 — Supplier list/detail/edit, product links

- **Feature:** [F04 — Suppliers](../../Features/F04-suppliers.md)
- **Status:** Done
- **Depends on:** WI-04-2, WI-02-3
- **Estimate:** M

## Goal

Users manage suppliers and their product links from both supplier and product pages.

## Scope

- In: `/Suppliers`, `/Suppliers/{id}`, `/Suppliers/Edit`, product detail Suppliers tab.
- Out: supplier POs list (added in WI-05-3).

## Tasks

- [x] `/Suppliers`: live search, status filter, sortable paged table (name, contact, phone, email, # products).
- [x] `/Suppliers/{id}`: contact card (mailto/tel links), linked products table with inline edit rows (HTMX), "Add product" via `_ProductPicker`, preferred star toggle.
- [x] `/Suppliers/Edit/{id?}`: form with validation; archive/restore.
- [x] Product detail "Suppliers" tab: same link table from the product side with "Add supplier" picker.
- [x] Page tests: create supplier, link product, toggle preferred updates both views.

## Acceptance criteria

- [x] Setting preferred on one supplier visibly un-sets the previous one without page reload.
- [x] Viewer sees read-only tables.

## Notes

Reuse one row partial for both views.

### Implementation notes

- **One shared endpoint instead of `?handler=Link`/`Unlink` on each page.** The spec sketched link handlers on `/Suppliers/{id}` and
  `/Products/{id}`. Both pages now post to `/Suppliers/Links` (`Save`, `Remove`, `Preferred` POST; `Edit`, `Row` GET) with a `view`
  field (`Supplier` or `Product`). Every action answers with the whole links block (`_SupplierLinks`, `#supplier-links` or
  `#product-suppliers`), so moving the preferred star visibly clears the previous one without a reload, and the two pages cannot drift.
  Without htmx the same posts redirect back to the owning page. `/Products/{id}?handler=Suppliers` was not needed: the block is
  rendered inline in the product page, reached from the "Suppliers" tab link (`#suppliers`).
- **Partials:** `_SupplierLinks` (table + "Add" form), `_SupplierLinkRow`, `_SupplierLinkEditRow` (inline edit), built by
  `SupplierLinksBuilder`. The first column is the product on the supplier page and the supplier on the product page.
- **Add form:** the supplier page uses `_ProductPicker`; the product page uses a `<select>` of the active, not-yet-linked suppliers
  (`ListUnlinkedForProductAsync`), since a business has few suppliers. Adding an existing pair updates its terms (service upsert).
- **Archived owners:** the add form is replaced by a hint; existing links stay visible and editable/removable.
- Links carry no row version: link edits are last-write-wins, and the preferred switch is made safe by the service transaction.
- Detail archive/restore follows the products page (plain post or `hx-post` + `hx-confirm`, ending on the refreshed page).
- The "Suppliers" nav item is now available; `LayoutAndComponentTests` was updated accordingly.
