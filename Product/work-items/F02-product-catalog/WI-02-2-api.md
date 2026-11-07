# WI-02-2 — ProductService, CategoryService

- **Feature:** [F02 — Product catalog](../../Features/F02-product-catalog.md)
- **Status:** Done
- **Depends on:** WI-02-1, WI-01-2
- **Estimate:** M

## Goal

Services implementing catalog queries and commands with all F02 business rules, plus HTTP handlers ready for UI.

## Scope

- In: services, input/output models, validation, page model handlers (logic only), lookup endpoint, tests.
- Out: Razor markup (WI-02-3).

## Tasks

- [x] `ProductListQuery : ListQuery` + `CategoryId`, `Status` (Active/Archived/All), sort keys (Sku, Name, OnHand, Category).
- [x] `ProductService.SearchAsync` → `PagedResult<ProductListItem>` (incl. on hand, low-stock flag using ReorderPoint for now; on-order added in F06).
- [x] `ProductService.FindByCodeAsync(code)` exact SKU or barcode match.
- [x] `GetAsync(id)` → `ProductDetail`; `CreateAsync`, `UpdateAsync` (RowVersion check), `SetActiveAsync`.
- [x] Rule: SKU immutable once product has stock transactions (check via `StockTransactions.Any` once F03 exists; until then always editable — leave TODO referencing WI-03-2).
- [x] Money input parsing: decimal string to cents (`MoneyParser`), invariant + current culture.
- [x] `LookupAsync(q, activeOnly, take=20)` for pickers.
- [x] `CategoryService`: list, create, update, set active; unique name check.
- [x] Map unique-constraint violations (`SqliteException` 2067) to field errors.
- [x] Tests: search/filter/sort/paging, duplicate SKU error, concurrency conflict, archive hides from lookup.

## Acceptance criteria

- [x] All FR-1..FR-6 rules covered by service tests.
- [x] Search over 10k seeded products returns in < 100 ms (test with seeded data, informational).

## Notes

Keep services UI-agnostic; page handlers call them and render partials.
## Implementation notes

- **Scope deviation:** the Razor page handlers (including `/Products/Lookup`) are not in this item. A Razor Page cannot exist without its `.cshtml`, so the thin handlers land with their markup in WI-02-3. The services here are the complete, tested business layer they call. The "Out: Razor markup" line still holds; this just records that "handlers (logic only)" moved too.
- **Update signature:** `UpdateAsync(id, rowVersion, ProductInput)` - the version the form was loaded with is passed separately. A stale version gives `product.conflict` (no field, shown at the top of the form). A lost race during the save is mapped to the same error.
- **Archived categories:** can stay on products that already have them but cannot be newly chosen (`product.category_archived`).
- **Money:** `MoneyParser` tries the current culture, then invariant, then invariant with *well-formed* thousands (`1,234.50`). It refuses more than two decimals instead of rounding, and never reads `1,5` as 15. Thousands separators are not accepted in other cultures' formats.
- **SKU lock:** `IsSkuLockedAsync` returns false with a `TODO(WI-03-2)`; the service and test comments mark where the ledger check goes.
- **FindByCodeAsync** matches archived products too (an exact code should always find its product); a SKU match wins over another product's barcode. A barcode equal to a *different* product's SKU is not rejected (not in the spec).
- **Shared helpers added:** `LikePattern` (wildcard-safe search, now also used by `UserService`), `DbErrors` (SQLite unique-violation detection).
- **Performance (10,000 products, in-memory SQLite, warm):** listing 3 ms, SKU search 19 ms, name 4 ms, barcode 6 ms, sorted+filtered 9 ms. The test fails only above 1 s so slow machines do not flake; the 100 ms target is informational.
