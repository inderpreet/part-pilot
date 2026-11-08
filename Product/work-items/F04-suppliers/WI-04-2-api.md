# WI-04-2 — SupplierService

- **Feature:** [F04 — Suppliers](../../Features/F04-suppliers.md)
- **Status:** Done
- **Depends on:** WI-04-1, WI-02-2
- **Estimate:** M

## Goal

Service layer for suppliers and product–supplier links, enforcing F04 rules.

## Scope

- In: `SupplierService`, models, validation, tests.
- Out: UI (WI-04-3).

## Tasks

- [x] `SearchAsync(SupplierListQuery)` (name/contact/email, status, paging, sort).
- [x] `GetAsync(id)` with linked products (SKU, name, supplier SKU, cost, lead time, preferred).
- [x] `CreateAsync`, `UpdateAsync`, `SetActiveAsync`; unique name, email format validation.
- [x] `LinkProductAsync(productId, supplierId, input)` upsert; `UnlinkProductAsync`.
- [x] `SetPreferredAsync(productId, supplierId)` clears other preferred flags in same transaction.
- [x] `GetForProductAsync(productId)` for product detail tab.
- [x] `LookupAsync(q, activeOnly)` for PO supplier picker.
- [x] Tests: unique name, preferred switching, archived supplier excluded from lookup, link upsert.

## Acceptance criteria

- [x] All F04 business rules covered by tests.

## Notes

—

## Implementation notes

- **API** (`Services/SupplierService.cs`, models in `SupplierModels.cs`, DI via `AddSuppliers()`): `SearchAsync`, `GetAsync`, `CreateAsync`, `UpdateAsync(id, rowVersion, input)`, `SetActiveAsync`, `GetProductsAsync(supplierId)` and `GetForProductAsync(productId)` (the two link lists; one `ProductSupplierItem` shape so one row partial serves both pages), `GetLinkAsync`, `LinkProductAsync` (upsert), `UnlinkProductAsync`, `SetPreferredAsync(productId, supplierId, preferred = true)`, `LookupAsync(q, activeOnly)` (for the PO supplier picker in F05) and `ListUnlinkedForProductAsync` (the choices of the product page's "Add supplier").
- **Supplier rules decided here:** names unique case-insensitively, archived ones included; `Email` must be a single plain `a@b.c` address; `Website` must be an http(s) URL (a bare `acme.example` is stored as `https://acme.example`), so a stored website is always safe to render as a link (`javascript:` and friends are rejected); the address keeps its line breaks; optimistic concurrency through `RowVersion` (`supplier.conflict`).
- **Link rules decided here:** a *new* link needs an active product and an active supplier; an existing link stays editable and removable after either is archived; an archived supplier cannot *become* preferred (it may stop being it); cost >= 0, lead time 0-3650 days or blank, minimum order 1-1,000,000,000, supplier SKU <= 64. Removing a link is idempotent and a hard delete (nothing references a link).
- **Preferred switching** runs in one transaction: the old flag is saved as cleared first (SQLite checks the filtered unique index per statement), then the new one is set. A race that still trips the index (or adds the same pair twice) comes back as `supplier.link_conflict` and nothing is saved.
- **Tests:** `Services/SupplierServiceTests` (create/update validation, formats, uniqueness, version conflicts and DB-level races, archive/restore, search filter/sort/paging/wildcards, link upsert, validation, archived rules, preferred switching and race, unlink, orderings, lookups, container resolution).
