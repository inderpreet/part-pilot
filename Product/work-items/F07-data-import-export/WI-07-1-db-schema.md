# WI-07-1 — ImportBatch schema, CSV formats

- **Feature:** [F07 — Data import/export & backup](../../Features/F07-data-import-export.md)
- **Status:** Planned
- **Depends on:** WI-03-1, WI-04-1
- **Estimate:** S

## Goal

Import traceability table exists and every CSV format (import + export) is specified precisely.

## Scope

- In: `ImportBatch` entity, FK from StockTransaction, migration, CSV column specs (this file).
- Out: parsing code (WI-07-2).

## Tasks

- [ ] `ImportBatch`: Id, Kind (string: Products, Suppliers, ProductSuppliers, OpeningStock), FileName (255), RowCount, CreatedCount,
      UpdatedCount, SkippedCount, CreatedById (FK users), CreatedAt.
- [ ] Add FK `StockTransaction.ImportBatchId → ImportBatch.Id` (`Restrict`).
- [ ] Migration `AddImportBatch`.
- [ ] Finalise CSV specs below; add template files to `wwwroot/templates/` (embedded).

## CSV specifications (draft)

**products.csv** — key: `sku`
`sku*, name*, description, category, unit, barcode, bin_location, reorder_point, reorder_quantity, unit_cost, active`

**suppliers.csv** — key: `name`
`name*, contact_name, email, phone, address, website, notes, active`

**product_suppliers.csv** — key: `sku + supplier`
`sku*, supplier*, supplier_sku, unit_cost, lead_time_days, min_order_quantity, preferred`

**opening_stock.csv** — posts `Count` transactions
`sku*, quantity*, note`

**Exports** add read-only columns: products `+ quantity_on_hand, created_at, updated_at`; transactions export:
`occurred_at, sku, product_name, type, quantity_delta, quantity_after, unit_cost, reason, reference, po_number, user`.

`*` = required. Booleans accept `true/false/yes/no/1/0`. Money `12.34` invariant. Dates ISO 8601.

## Acceptance criteria

- [ ] Specs reviewed and templates downloadable; migration applies.

## Notes

—
