# WI-07-2 — Import/Export/Backup services, permission policies

- **Feature:** [F07 — Data import/export & backup](../../Features/F07-data-import-export.md)
- **Status:** Done
- **Depends on:** WI-07-1, WI-03-2, WI-04-2, WI-10-2
- **Estimate:** L

## Goal

Validated, all-or-nothing CSV imports (parts, suppliers, links, stock, BOMs) via existing services; streaming exports; consistent
online DB backup; authorization policies that follow the Admin's permission settings.

## Scope

- In: `ImportService`, `ExportService`, `BackupService`, permission policies, tests.
- Out: UI (WI-07-3).

## Tasks

- [x] CsvHelper is already referenced (D21). Parser per kind → `ImportRow<T>` with row number, parsed values, errors.
- [x] `PreviewAsync(kind, options, stream)` → rows with action (Create/Update/Skip/Error) without writing; store parsed upload in temp file keyed by token (expires 30 min).
- [x] `CommitAsync(token, skipInvalid)`: one DB transaction; create `ImportBatch`; apply via `ProductService` / `SupplierService` / `StockService` (Count with ImportBatchId) / `ProjectService`.
- [x] BOM import: group rows by project number; match by number; blank number = create with generated number; unknown number = create with it;
      Merge adds and updates lines, Replace also removes the project's lines that are not in the file (preview shows the count removed);
      missing SKU = row error unless `createMissingParts`, then a part is made from `part_name` / `part_unit` / `part_unit_cost`;
      disagreeing project fields = error on the later rows; header-only rows allowed.
- [x] BOM import goes through `ProjectService` (name/number uniqueness, one line per part, only an Admin changes an existing number: a number column is ignored for matching only, never used to rename).
- [x] Rules: header case-insensitive; required columns; duplicate keys within file = error; blank cell on update = unchanged; category auto-create.
- [x] Limits: 10 MB, 50k rows.
- [x] `ExportService`: parts, suppliers, links, transactions(date range), BOMs (one or all projects) — streamed with `CsvExport`.
- [x] `BackupService.CreateSnapshotAsync()` → `VACUUM INTO` temp path; caller streams and deletes.
- [x] Permissions: a `DataPermission` enum (ImportData, ImportBoms, ExportData, ExportBoms), `PermissionRequirement` + handler reading
      `ISettingsService` (Admin always passes; Staff and Viewer by their setting), policies `CanImportData`, `CanImportBoms`, `CanExportData`,
      `CanExportBoms` plus `CanImport` and `CanExport` (either of the two). Every policy repeats the active-user check. Changes apply without restart (settings cache reset).
- [x] Audit hooks: `IImportEvents` (`ImportCommitted`) and `BackupDownloaded`, fan-out publisher like `IProjectEvents`.
- [x] Tests: each kind create/update, error rows block commit, skipInvalid commits valid rows, round-trip export → import yields no changes
      (including BOMs), BOM merge vs replace, create-missing-parts, unknown SKU error, a failed commit leaves the DB unchanged, backup file opens and has same row counts,
      permission handler matrix (Admin/Staff/Viewer × allow/deny × four permissions, inactive user denied).

## Acceptance criteria

- [x] Importing 10k products completes in < 30 s. (Measured 9.7 s for preview + commit on 2026-11-09; `ImportBenchmarks`, run with `INVENTORY_BENCH=1`.)
- [x] Failed commit leaves DB unchanged.
- [x] Changing a permission setting changes the next request's answer.

## Notes

Import must not bypass service rules (e.g. negative stock, SKU lock). The import runs as the signed-in user; `ProjectActor.IsAdmin` is
taken from that user, so a Staff member with "Import BOMs" cannot rename a project number.

Field rules are shared with the preview through `ProductService.ValidateFields`, `SupplierService.ValidateFields` / `ValidateLink` and
`ProjectService.ValidateProject` / `ValidateLine`, so a new rule there reaches the preview too. The apply loops clear the change tracker between
rows; without that, 10k parts took 200 s. Issues found in review: [2026-11-09 report](../../reviews/2026-11-09-1204-F07-code-review.md).
