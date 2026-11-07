# WI-04-3 — Supplier list/detail/edit, product links

- **Feature:** [F04 — Suppliers](../../Features/F04-suppliers.md)
- **Status:** Planned
- **Depends on:** WI-04-2, WI-02-3
- **Estimate:** M

## Goal

Users manage suppliers and their product links from both supplier and product pages.

## Scope

- In: `/Suppliers`, `/Suppliers/{id}`, `/Suppliers/Edit`, product detail Suppliers tab.
- Out: supplier POs list (added in WI-05-3).

## Tasks

- [ ] `/Suppliers`: live search, status filter, sortable paged table (name, contact, phone, email, # products).
- [ ] `/Suppliers/{id}`: contact card (mailto/tel links), linked products table with inline edit rows (HTMX), "Add product" via `_ProductPicker`, preferred star toggle.
- [ ] `/Suppliers/Edit/{id?}`: form with validation; archive/restore.
- [ ] Product detail "Suppliers" tab: same link table from the product side with "Add supplier" picker.
- [ ] Page tests: create supplier, link product, toggle preferred updates both views.

## Acceptance criteria

- [ ] Setting preferred on one supplier visibly un-sets the previous one without page reload.
- [ ] Viewer sees read-only tables.

## Notes

Reuse one `_ProductSupplierRow` partial for both views.
