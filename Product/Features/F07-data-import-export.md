# F07 — Data import/export & backup

- **Status:** Done
- **Depends on:** F03, F04, F10
- **Roles:** Admin always; Staff and Viewers per the permissions an Admin grants (FR-9). Backup is Admin only.

## Summary

Get data in and out: bulk-load parts, suppliers, supplier prices, opening stock and **BOMs (projects with their parts)** from CSV
with a validated preview, export the same data and the stock ledger to CSV, and download a consistent backup of the SQLite
database. An Admin decides which roles may import and export, separately for master data and BOMs.

## User stories

- As an Admin setting up the system, I want to import my parts list and opening stock from a spreadsheet.
- As an Admin, I want to update many parts at once (e.g. new costs) by re-importing a CSV keyed by SKU.
- As an Admin, I want to load BOMs for many projects from one spreadsheet, and either merge into or replace an existing BOM.
- As Staff, I want to export parts, stock and BOMs to CSV for use in Excel.
- As an Admin, I want to choose whether Staff or Viewers may import or export, and I want BOMs controlled separately from parts.
- As an Admin, I want to download a backup of the whole database while the app is running.

## Functional requirements

1. FR-1 Import kinds: **Parts** (create or update by SKU; category created if missing), **Suppliers** (create or update by name),
   **Part–supplier links** (SKU + supplier name), **Opening stock / stock count** (SKU + quantity → `Count` transactions),
   **BOMs** (project + BOM lines, FR-8).
2. FR-2 Import flow: upload CSV (max 10 MB) → parse + validate → preview table showing per row: action (create/update/skip) and errors →
   commit (all-or-nothing in one DB transaction) → summary. Rows with errors block commit unless "skip invalid rows" chosen.
3. FR-3 Downloadable CSV templates for each import kind with header row and an example line.
4. FR-4 Exports: parts (incl. on hand), suppliers, part–supplier links, stock transactions (date range), **BOMs** (FR-8).
   UTF-8 with BOM for Excel. Text cells that start with `= + - @` are neutralised (D21).
5. FR-5 Every committed import is recorded as an `ImportBatch`; stock transactions created by it reference the batch.
6. FR-6 Backup download: `VACUUM INTO` temp file, streamed as `inventory-<yyyyMMdd-HHmm>.db`, then deleted.
7. FR-7 Restore is documented as a manual procedure (stop app, replace file) — not an in-app action in v1.
8. FR-8 **BOM import/export** (see "BOM CSV" below). One flat file, one row per BOM line, so a single file can carry many projects.
   - A project is matched by **project number**. A blank number creates a new project with a generated number. A number that
     does not exist creates a project with that number (so a file exported from another install loads as it was).
   - The upload screen has a **mode**: *Merge* (add new parts, update quantity and note, never remove) or *Replace*
     (the file becomes the BOM: lines of a project in the file that are not in it are removed). Replace is described in the
     preview with the number of lines it removes, per project. Projects not in the file are never touched.
   - A part (SKU) that is not in the catalog is a row error. A checkbox **"Create missing parts"** on the upload screen turns
     that into a create, using the `part_name` (required then), `part_unit` and `part_unit_cost` columns of the row.
   - Project fields (`project_name`, `target_units`, `description`, `notes`) repeat on each row of the project. When rows of one
     project disagree, the first row wins and the others get an error. A project row with a blank `sku` and blank quantity
     is allowed: it creates or updates the project header only (this is how an empty project round-trips).
   - Applied through `ProjectService`, so the F10 rules hold (unique name and number, one line per part, built projects stay as
     they are, only an Admin changes the number of an existing project).
   - The export writes exactly this format, so exporting and importing it again changes nothing (round trip). It carries lines only,
     no status or build history.
9. FR-9 **Permissions.** An Admin grants or denies, per role, four permissions on the Settings page (Staff and Viewer columns;
   Admin always has all four and cannot be locked out). Backup is not a permission: it is always Admin only, because the file
   holds password hashes.

   | Permission | Covers | Staff default | Viewer default |
   |---|---|---|---|
   | Import data | parts, suppliers, part–supplier links, opening stock | Deny | Deny |
   | Import BOMs | BOM import | Deny | Deny |
   | Export data | parts, suppliers, links, stock transactions | Allow | Deny |
   | Export BOMs | BOM export | Allow | Deny |

   Each permission is an authorization policy (`CanImportData`, `CanImportBoms`, `CanExportData`, `CanExportBoms`); pages and
   navigation use them like the other policies, and the answer follows a change on the Settings page without a restart.
   Importing creates and changes records through the services, so every role check the services apply still holds; the
   permission only decides who may open the import.

## Business rules

- Import header matching is case-insensitive; unknown columns ignored; required columns enforced.
- Money columns accept `12.34` (invariant culture); quantities whole numbers.
- Blank cell on update = leave unchanged (not clear), except explicit `""` handling is not supported.
- A stock-count import and a BOM *Replace* are previewed with what they will change before anything is written.
- Imports are audited (F08) as one summary entry per batch, not per row, through an event hook (no audit rows are written by this feature).
- The preview reports every rule the services would refuse as a row error (archived parts and categories, field lengths, supplier email and website, duplicate barcodes, BOM name collisions), so "skip invalid rows" can exclude them. A service refusal that still reaches commit rolls everything back, names the row and keeps the upload so the preview can be shown again.
- A BOM *Replace* leaves a project completely unchanged when any of its rows is invalid, even with "skip invalid rows"; *Merge* applies that project's valid rows.
- A stray quote inside a plain field (`3.5" screw`) is kept as typed. A quoted field that is not closed properly is a file error.
- Import removes the apostrophe the export adds to neutralise formulas (FR-4), so an exported file imports back unchanged.

## BOM CSV

`bom.csv` — key: `project_number` (+ `sku` within the project)

`project_number, project_name*, target_units, description, notes, sku, quantity_per_unit, note, part_name, part_unit, part_unit_cost`

- `project_name` is required on every row. `target_units` defaults to 1 on a new project and is left alone on an existing one when blank.
- `sku` and `quantity_per_unit` are required together (whole number ≥ 1); both blank = project header only.
- `part_name`, `part_unit`, `part_unit_cost` are read only when the part is missing and **Create missing parts** is ticked.
- The export adds nothing and drops nothing: `part_name`, `part_unit` and `part_unit_cost` are filled from the catalog so the file can be
  loaded into an install that does not have the parts yet.

## Data model

`ImportBatch` — see architecture §5 (Kind adds `Boms`; `Options` records mode and create-missing for BOM imports).
CSV column specs in WI-07-1. Permissions are stored as settings (`Permission.<Name>.Staff`, `Permission.<Name>.Viewer`), no new table.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Import` | Choose kind, upload, preview, commit, history | `CanImportData` or `CanImportBoms` (each kind needs its own) |
| `/Import?handler=Template&kind={kind}` | Download CSV template | the kind's import policy |
| `/Import?batch={id}` | Summary of one committed import (linked from the history list) | `CanImportData` or `CanImportBoms` |
| `/Stock/Movements?importBatchId={id}` | Stock movements posted by one opening-stock import | `CanView` |
| `/Export` | Export menu + downloads | `CanExportData` or `CanExportBoms` (each export needs its own) |
| `/Admin/Backup` | Download DB backup | RequireAdmin |
| `/Admin/Settings` | "Data permissions" card (matrix of the four permissions by role) | RequireAdmin |

## Out of scope

- Excel (.xlsx) files, scheduled backups, in-app restore, PO import (the first two are parked in [WI-07-4](../work-items/F07-data-import-export/WI-07-4-xlsx-and-scheduled-backups.md)).
- BOM status, build history and cost columns in the export; sub-assemblies and alternates (not in F10 either).

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-07-1](../work-items/F07-data-import-export/WI-07-1-db-schema.md) | ImportBatch schema, CSV formats, permission settings | Done |
| [WI-07-2](../work-items/F07-data-import-export/WI-07-2-api.md) | Import/Export/Backup services, permission policies | Done |
| [WI-07-3](../work-items/F07-data-import-export/WI-07-3-frontend.md) | Upload, preview, export, backup pages, permissions card | Done |
| [WI-07-4](../work-items/F07-data-import-export/WI-07-4-xlsx-and-scheduled-backups.md) | .xlsx import and scheduled backups | Blocked |

## Open questions

- .xlsx import and automatic scheduled backups to a folder (nightly, keep last N) are wanted, but later: see WI-07-4.
- Should a Viewer ever be allowed to import? The matrix allows it for completeness; the defaults deny it. Revisit if Admins ask to remove the option.

## Decisions (this implementation)

- Nav: Import and Export sit in the Insights group, after Reports. Each is shown only when the matching policy passes (`CanImport` / `CanExport`).
- An existing **archived** project named in a BOM CSV is updated in place and stays archived.
- **Replace** (and Merge) is allowed on a project that already has builds; the preview lists lines that Replace will remove. Builds are not imported or exported.
- Commit re-checks the permission for the staged kind, so a permission revoked after the preview applies at once.
- Staged uploads are deleted on commit, cancel and expiry; files left by a crashed process are swept at the next start once past the 30-minute limit.

## Reviews

- [2026-11-09 12:04 code review](../reviews/2026-11-09-1204-F07-code-review.md): 26 issues, all resolved.
