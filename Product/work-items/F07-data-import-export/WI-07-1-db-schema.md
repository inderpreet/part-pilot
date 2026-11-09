# WI-07-1 — ImportBatch schema, CSV formats, permission settings

- **Feature:** [F07 — Data import/export & backup](../../Features/F07-data-import-export.md)
- **Status:** Planned
- **Depends on:** WI-03-1, WI-04-1, WI-10-1
- **Estimate:** S

## Goal

Import traceability table exists, every CSV format (import + export, including BOMs) is specified precisely, and the four
data permissions have settings keys with defaults.

## Scope

- In: `ImportBatch` entity, FK from StockTransaction, migration, CSV column specs (this file), permission setting keys and defaults.
- Out: parsing code and policies (WI-07-2), screens (WI-07-3).

## Tasks

- [ ] `ImportBatch`: Id, Kind (string: Products, Suppliers, ProductSuppliers, OpeningStock, Boms), FileName (255), RowCount, CreatedCount,
      UpdatedCount, SkippedCount, Options (string?, 200: for BOMs `mode=Merge|Replace;createMissingParts=true|false`), CreatedById (FK users), CreatedAt.
- [ ] Add FK `StockTransaction.ImportBatchId → ImportBatch.Id` (`Restrict`); the column exists already, so the migration adds the table and the FK.
- [ ] Migration `AddImportBatch` (applies to an empty DB and to the previous schema).
- [ ] Setting keys with defaults in `SettingKeys`: `perm.import_data.staff`, `perm.import_data.viewer`, `perm.import_boms.staff`,
      `perm.import_boms.viewer`, `perm.export_data.staff`, `perm.export_data.viewer`, `perm.export_boms.staff`, `perm.export_boms.viewer`
      (defaults: Staff exports allowed, everything else denied). Unknown keys stay rejected by `SettingsService`.
- [ ] Finalise CSV specs below; add template files to `wwwroot/templates/` (embedded).
- [ ] Schema tests: table and FK exist, `Restrict` delete of a batch with transactions fails, defaults present.

## CSV specifications

**products.csv** — key: `sku`
`sku*, name*, description, category, unit, barcode, bin_location, reorder_point, reorder_quantity, unit_cost, active`

**suppliers.csv** — key: `name`
`name*, contact_name, email, phone, address, website, notes, active`

**product_suppliers.csv** — key: `sku + supplier`
`sku*, supplier*, supplier_sku, unit_cost, lead_time_days, min_order_quantity, preferred`

**opening_stock.csv** — posts `Count` transactions
`sku*, quantity*, note`

**bom.csv** — key: `project_number` (+ `sku` within the project); one row per BOM line
`project_number, project_name*, target_units, description, notes, sku, quantity_per_unit, note, part_name, part_unit, part_unit_cost`

- `sku` and `quantity_per_unit` go together (whole number ≥ 1); both blank = project header only.
- `part_name`, `part_unit`, `part_unit_cost` are used only to create a missing part when "Create missing parts" is ticked; `part_name` is then required.
- Project fields repeat on every row of the project; the first row wins, a disagreeing later row is an error.
- Export fills `part_*` from the catalog and writes every project the user selects (all active by default) in number order, lines in SKU order.

**Exports** add read-only columns: products `+ quantity_on_hand, created_at, updated_at`; transactions export:
`occurred_at, sku, product_name, type, quantity_delta, quantity_after, unit_cost, reason, reference, po_number, user`.

`*` = required. Booleans accept `true/false/yes/no/1/0`. Money `12.34` invariant. Dates ISO 8601.

## Acceptance criteria

- [ ] Specs reviewed and templates downloadable; migration applies.
- [ ] The eight permission settings exist with the documented defaults.

## Notes

The permission settings live in `AppSettings` (no new table); the Settings page edits them in WI-07-3 and policies read them in WI-07-2.
