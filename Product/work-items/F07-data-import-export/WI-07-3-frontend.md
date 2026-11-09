# WI-07-3 — Upload, preview, export, backup pages, permissions card

- **Feature:** [F07 — Data import/export & backup](../../Features/F07-data-import-export.md)
- **Status:** Done
- **Depends on:** WI-07-2
- **Estimate:** M

## Goal

Users with the permission import data through a guided upload → preview → commit flow and export CSVs; Admins download backups
and decide who may import and export.

## Scope

- In: `/Import`, `/Export`, `/Admin/Backup`, nav entries, "Data permissions" card on `/Admin/Settings`.
- Out: restore UI.

## Tasks

- [x] `/Import` step 1: choose kind (only the kinds the user may import), template download link, file input (`hx-encoding="multipart/form-data"`).
      For BOMs also a **mode** (Merge / Replace) and a **Create missing parts** checkbox.
- [x] Step 2 preview: counts (create/update/skip/error), table with row number, action badge, error messages; filter "errors only";
      "skip invalid rows" checkbox; Commit / Cancel. BOM preview groups rows by project, shows the lines a Replace removes; a danger confirm on Replace.
- [x] Step 3 summary: counts, link to batch (and to stock movements filtered by batch for opening stock; to the projects for BOMs).
- [x] Import history list (recent `ImportBatch` rows).
- [x] `/Export`: buttons per export the user may use; date range for transactions; BOM export of all active projects or one project; "include archived" tick.
- [x] `/Admin/Backup`: explanation + "Download backup" button; manual restore instructions.
- [x] Settings page: "Data permissions" card, a matrix of the four permissions by Staff and Viewer (Admin column shown as always allowed); saved with the other settings or its own Save.
- [x] Nav: "Import" and "Export" items in the Insights group (after Reports), visible by `CanImport` / `CanExport`; a BOM page "Export BOM" action and an Import link on the BOM list for users who may.
- [x] Page tests: upload invalid CSV shows errors; a role without the permission gets 403 and the nav item is hidden; granting the permission on the Settings page opens it;
      export returns CSV; BOM merge and replace end to end; Viewer cannot mutate.

## Acceptance criteria

- [x] Full opening-stock import from template works end to end and on-hand values match file.
- [x] A BOM CSV for three projects imports end to end; exporting it and importing again changes nothing.
- [x] Backup download opens in a SQLite browser with all tables.
- [x] Layout checked at 390, 768 and 1440 px.

## Notes

Use the F09 patterns (page header, list card, status chips, confirm dialog); see architecture D17.
WI-07-4 (.xlsx and scheduled backups) stays blocked until picked up. Layout at 390/768/1440 measured in headless Edge on 2026-11-09 (preview, upload, summary, export, backup, settings, batch movements): no horizontal page overflow.
Issues found in review: [2026-11-09 report](../../reviews/2026-11-09-1204-F07-code-review.md).
