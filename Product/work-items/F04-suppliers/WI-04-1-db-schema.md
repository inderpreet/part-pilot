# WI-04-1 — Supplier, ProductSupplier schema

- **Feature:** [F04 — Suppliers](../../Features/F04-suppliers.md)
- **Status:** Planned
- **Depends on:** WI-02-1
- **Estimate:** S

## Goal

Supplier directory and product–supplier link tables with constraints.

## Scope

- In: entities, configurations, migration.
- Out: PO tables (WI-05-1).

## Tasks

- [ ] `Supplier`: Id, Name (max 200, `NOCASE`, unique), ContactName (100), Email (200), Phone (50), Address (500), Website (200),
      Notes (2000), IsActive, timestamps.
- [ ] `ProductSupplier`: composite PK (ProductId, SupplierId), both FKs `Restrict` (parents are archived, never deleted);
      SupplierSku (max 64), UnitCostCents long (>= 0), LeadTimeDays int? (>= 0), MinOrderQuantity int (>= 1, default 1), IsPreferred bool, timestamps.
- [ ] Unique filtered index `(ProductId) WHERE IsPreferred = 1` — one preferred supplier per product.
- [ ] Index `SupplierId`.
- [ ] Migration `AddSuppliers`.

## Acceptance criteria

- [ ] Second preferred supplier for same product violates the filtered unique index.
- [ ] Migration applies on top of previous schema.

## Notes

—
