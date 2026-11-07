# WI-07-2 — Import/Export/Backup services

- **Feature:** [F07 — Data import/export & backup](../../Features/F07-data-import-export.md)
- **Status:** Planned
- **Depends on:** WI-07-1, WI-03-2, WI-04-2
- **Estimate:** L

## Goal

Validated, all-or-nothing CSV imports via existing services; streaming exports; consistent online DB backup.

## Scope

- In: `ImportService`, `ExportService`, `BackupService`, tests.
- Out: UI (WI-07-3).

## Tasks

- [ ] Add CsvHelper. Parser per kind → `ImportRow<T>` with row number, parsed values, errors.
- [ ] `PreviewAsync(kind, stream)` → rows with action (Create/Update/Skip/Error) without writing; store parsed upload in temp file keyed by token (expires 30 min).
- [ ] `CommitAsync(token, skipInvalid)`: one DB transaction; create `ImportBatch`; apply via `ProductService` / `SupplierService` / `StockService` (Count with ImportBatchId).
- [ ] Rules: header case-insensitive; required columns; duplicate keys within file = error; blank cell on update = unchanged; category auto-create.
- [ ] Limits: 10 MB, 50k rows.
- [ ] `ExportService`: products, suppliers, links, transactions(date range) — streamed with `CsvExport`.
- [ ] `BackupService.CreateSnapshotAsync()` → `VACUUM INTO` temp path; caller streams and deletes.
- [ ] Audit hooks: `ImportCommitted`, `BackupDownloaded`.
- [ ] Tests: each kind create/update, error rows block commit, skipInvalid commits valid rows, round-trip export → import yields no changes, backup file opens and has same row counts.

## Acceptance criteria

- [ ] Importing 10k products completes in < 30 s.
- [ ] Failed commit leaves DB unchanged.

## Notes

Import must not bypass service rules (e.g. negative stock, SKU lock).
