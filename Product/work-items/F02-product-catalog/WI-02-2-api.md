# WI-02-2 — ProductService, CategoryService

- **Feature:** [F02 — Product catalog](../../Features/F02-product-catalog.md)
- **Status:** Planned
- **Depends on:** WI-02-1, WI-01-2
- **Estimate:** M

## Goal

Services implementing catalog queries and commands with all F02 business rules, plus HTTP handlers ready for UI.

## Scope

- In: services, input/output models, validation, page model handlers (logic only), lookup endpoint, tests.
- Out: Razor markup (WI-02-3).

## Tasks

- [ ] `ProductListQuery : ListQuery` + `CategoryId`, `Status` (Active/Archived/All), sort keys (Sku, Name, OnHand, Category).
- [ ] `ProductService.SearchAsync` → `PagedResult<ProductListItem>` (incl. on hand, low-stock flag using ReorderPoint for now; on-order added in F06).
- [ ] `ProductService.FindByCodeAsync(code)` exact SKU or barcode match.
- [ ] `GetAsync(id)` → `ProductDetail`; `CreateAsync`, `UpdateAsync` (RowVersion check), `SetActiveAsync`.
- [ ] Rule: SKU immutable once product has stock transactions (check via `StockTransactions.Any` once F03 exists; until then always editable — leave TODO referencing WI-03-2).
- [ ] Money input parsing: decimal string to cents (`MoneyParser`), invariant + current culture.
- [ ] `LookupAsync(q, activeOnly, take=20)` for pickers.
- [ ] `CategoryService`: list, create, update, set active; unique name check.
- [ ] Map unique-constraint violations (`SqliteException` 2067) to field errors.
- [ ] Tests: search/filter/sort/paging, duplicate SKU error, concurrency conflict, archive hides from lookup.

## Acceptance criteria

- [ ] All FR-1..FR-6 rules covered by service tests.
- [ ] Search over 10k seeded products returns in < 100 ms (test with seeded data, informational).

## Notes

Keep services UI-agnostic; page handlers call them and render partials.
