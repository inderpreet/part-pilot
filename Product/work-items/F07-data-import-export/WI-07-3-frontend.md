# WI-07-3 — Upload, preview, export, backup pages

- **Feature:** [F07 — Data import/export & backup](../../Features/F07-data-import-export.md)
- **Status:** Planned
- **Depends on:** WI-07-2
- **Estimate:** M

## Goal

Admins import data through a guided upload → preview → commit flow; users export CSVs; Admins download backups.

## Scope

- In: `/Admin/Import`, `/Export`, `/Admin/Backup`, Admin menu entries.
- Out: restore UI.

## Tasks

- [ ] `/Admin/Import` step 1: choose kind, template download link, file input (`hx-encoding="multipart/form-data"`).
- [ ] Step 2 preview: counts (create/update/skip/error), table with row number, action badge, error messages; filter "errors only"; "skip invalid rows" checkbox; Commit / Cancel.
- [ ] Step 3 summary: counts, link to batch (and to stock movements filtered by batch for opening stock).
- [ ] Import history list (recent `ImportBatch` rows).
- [ ] `/Export`: buttons per export; date range for transactions.
- [ ] `/Admin/Backup`: explanation + "Download backup" button; manual restore instructions.
- [ ] Page tests: upload invalid CSV shows errors; Staff cannot access `/Admin/Import`; export returns CSV.

## Acceptance criteria

- [ ] Full opening-stock import from template works end to end and on-hand values match file.
- [ ] Backup download opens in a SQLite browser with all tables.

## Notes

—
