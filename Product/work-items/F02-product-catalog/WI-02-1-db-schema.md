# WI-02-1 — Product, Category schema

- **Feature:** [F02 — Product catalog](../../Features/F02-product-catalog.md)
- **Status:** Done
- **Depends on:** WI-01-1
- **Estimate:** S

## Goal

`Category` and `Product` tables exist with constraints that enforce uniqueness and value ranges at the DB level.

## Scope

- In: entities, configurations, indexes, check constraints, migration.
- Out: stock ledger (WI-03-1) — `QuantityOnHand` column is added here but written only by F03.

## Tasks

- [x] `Category`: Id, Name (max 100, `NOCASE`, unique), Description (max 500), IsActive, timestamps.
- [x] `Product`: Id, Sku (max 40, `NOCASE`, unique), Name (max 200), Description (max 2000), CategoryId (FK, nullable, `Restrict`),
      Unit (max 20, default `each`), Barcode (max 64, `NOCASE`, unique filtered `WHERE Barcode IS NOT NULL`),
      BinLocation (max 50), ReorderPoint int, ReorderQuantity int, UnitCostCents long, QuantityOnHand int (default 0),
      IsActive, RowVersion long, timestamps.
- [x] Check constraints: `ReorderPoint >= 0`, `ReorderQuantity >= 0`, `UnitCostCents >= 0`.
- [x] Indexes: `Name`, `CategoryId`, `(IsActive, Name)`.
- [x] Migration `AddCatalog`.
- [x] Test: duplicate SKU differing only by case fails; null barcodes allowed multiple times.

## Acceptance criteria

- [x] Migration applies on top of previous schema; constraints verified by tests.

## Notes

`NOCASE` only folds ASCII — acceptable for SKUs/names in v1 (note in architecture if non-ASCII needed).

## Implementation notes

- `Product.Name` also uses `NOCASE` (not in the original task list) so `ORDER BY Name` is case-insensitive and can use the Name indexes.
- Unique-index and check-constraint names surface in SQLite error text as `Products.Sku` / `Products.Barcode` / `Categories.Name` and `CHECK constraint failed: CK_Products_*` - WI-02-2 maps these to field errors.
- Barcodes must be `null` (never an empty string) when absent; the services normalise this.
- Migration `AddCatalog` verified on top of the F01 schema with existing data (`CatalogSchemaTests`).
