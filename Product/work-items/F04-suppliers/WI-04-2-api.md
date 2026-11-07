# WI-04-2 — SupplierService

- **Feature:** [F04 — Suppliers](../../Features/F04-suppliers.md)
- **Status:** Planned
- **Depends on:** WI-04-1, WI-02-2
- **Estimate:** M

## Goal

Service layer for suppliers and product–supplier links, enforcing F04 rules.

## Scope

- In: `SupplierService`, models, validation, tests.
- Out: UI (WI-04-3).

## Tasks

- [ ] `SearchAsync(SupplierListQuery)` (name/contact/email, status, paging, sort).
- [ ] `GetAsync(id)` with linked products (SKU, name, supplier SKU, cost, lead time, preferred).
- [ ] `CreateAsync`, `UpdateAsync`, `SetActiveAsync`; unique name, email format validation.
- [ ] `LinkProductAsync(productId, supplierId, input)` upsert; `UnlinkProductAsync`.
- [ ] `SetPreferredAsync(productId, supplierId)` clears other preferred flags in same transaction.
- [ ] `GetForProductAsync(productId)` for product detail tab.
- [ ] `LookupAsync(q, activeOnly)` for PO supplier picker.
- [ ] Tests: unique name, preferred switching, archived supplier excluded from lookup, link upsert.

## Acceptance criteria

- [ ] All F04 business rules covered by tests.

## Notes

—
