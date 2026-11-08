# WI-04-1 — Supplier, ProductSupplier schema

- **Feature:** [F04 — Suppliers](../../Features/F04-suppliers.md)
- **Status:** Done
- **Depends on:** WI-02-1
- **Estimate:** S

## Goal

Supplier directory and product–supplier link tables with constraints.

## Scope

- In: entities, configurations, migration.
- Out: PO tables (WI-05-1).

## Tasks

- [x] `Supplier`: Id, Name (max 200, `NOCASE`, unique), ContactName (100), Email (200), Phone (50), Address (500), Website (200),
      Notes (2000), IsActive, timestamps.
- [x] `ProductSupplier`: composite PK (ProductId, SupplierId), both FKs `Restrict` (parents are archived, never deleted);
      SupplierSku (max 64), UnitCostCents long (>= 0), LeadTimeDays int? (>= 0), MinOrderQuantity int (>= 1, default 1), IsPreferred bool, timestamps.
- [x] Unique filtered index `(ProductId) WHERE IsPreferred = 1` — one preferred supplier per product.
- [x] Index `SupplierId`.
- [x] Migration `AddSuppliers`.

## Acceptance criteria

- [x] Second preferred supplier for same product violates the filtered unique index.
- [x] Migration applies on top of previous schema.

## Notes

—

## Implementation notes

- **Entities / configuration:** `Data/Entities/{Supplier,ProductSupplier}.cs`, `Data/Configurations/SupplierConfiguration.cs`, migration `AddSuppliers`. `Product.Suppliers` / `Supplier.Products` are the navigation properties.
- **Deviation: `Supplier.RowVersion`.** Added (as on `Product`) so two people editing the same supplier get a conflict message instead of silently overwriting each other. `ProductSupplier` has no row version: a link is a handful of small fields, and last write wins is the right behaviour for it. Architecture §5 updated.
- **Link FKs are `Restrict`** and a link is a real row that the service deletes on "Unlink" (nothing references it; POs keep their own cost and SKU copies).
- **Preferred:** filtered unique index `UX_ProductSuppliers_Preferred` on `ProductId WHERE IsPreferred = 1`. Switching preferred therefore has to clear the old flag before setting the new one (WI-04-2 does both in one transaction).
- **EF gotcha:** `MinOrderQuantity` has DB default 1, which EF treats as "unset" for a CLR value of 0 and silently replaces by 1. The property is `ValueGeneratedNever`, so a bad 0 reaches the CHECK constraint (and the service validates before that).
- **Tests:** `Data/SupplierSchemaTests` (tables/indexes, no pending model changes, case-insensitive unique name incl. archived, defaults, composite key, preferred index, CHECKs, FKs, concurrency, migration on top of the previous schema and rollback). `StockSchemaTests` rollback test no longer assumes AddStockLedger is the latest migration.
