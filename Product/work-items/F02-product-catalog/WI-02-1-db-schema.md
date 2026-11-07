# WI-02-1 — Product, Category schema

- **Feature:** [F02 — Product catalog](../../Features/F02-product-catalog.md)
- **Status:** Planned
- **Depends on:** WI-01-1
- **Estimate:** S

## Goal

`Category` and `Product` tables exist with constraints that enforce uniqueness and value ranges at the DB level.

## Scope

- In: entities, configurations, indexes, check constraints, migration.
- Out: stock ledger (WI-03-1) — `QuantityOnHand` column is added here but written only by F03.

## Tasks

- [ ] `Category`: Id, Name (max 100, `NOCASE`, unique), Description (max 500), IsActive, timestamps.
- [ ] `Product`: Id, Sku (max 40, `NOCASE`, unique), Name (max 200), Description (max 2000), CategoryId (FK, nullable, `Restrict`),
      Unit (max 20, default `each`), Barcode (max 64, `NOCASE`, unique filtered `WHERE Barcode IS NOT NULL`),
      BinLocation (max 50), ReorderPoint int, ReorderQuantity int, UnitCostCents long, QuantityOnHand int (default 0),
      IsActive, RowVersion long, timestamps.
- [ ] Check constraints: `ReorderPoint >= 0`, `ReorderQuantity >= 0`, `UnitCostCents >= 0`.
- [ ] Indexes: `Name`, `CategoryId`, `(IsActive, Name)`.
- [ ] Migration `AddCatalog`.
- [ ] Test: duplicate SKU differing only by case fails; null barcodes allowed multiple times.

## Acceptance criteria

- [ ] Migration applies on top of previous schema; constraints verified by tests.

## Notes

`NOCASE` only folds ASCII — acceptable for SKUs/names in v1 (note in architecture if non-ASCII needed).
