# F07 — Data import/export & backup

- **Status:** Planned
- **Depends on:** F03, F04
- **Roles:** Admin imports and backs up; Admin/Staff export

## Summary

Get data in and out: bulk-load products, suppliers and opening stock from CSV with a validated preview,
export master data and ledger to CSV, and download a consistent backup of the SQLite database.

## User stories

- As an Admin setting up the system, I want to import my product list and opening stock from a spreadsheet.
- As an Admin, I want to update many products at once (e.g. new costs) by re-importing a CSV keyed by SKU.
- As Staff, I want to export products/stock to CSV for use in Excel.
- As an Admin, I want to download a backup of the whole database while the app is running.

## Functional requirements

1. FR-1 Import kinds: **Products** (create or update by SKU; category created if missing), **Suppliers** (create or update by name),
   **Product–supplier links** (SKU + supplier name), **Opening stock / stock count** (SKU + quantity → `Count` transactions).
2. FR-2 Import flow: upload CSV (max 10 MB) → parse + validate → preview table showing per row: action (create/update/skip) and errors →
   commit (all-or-nothing in one DB transaction) → summary. Rows with errors block commit unless "skip invalid rows" chosen.
3. FR-3 Downloadable CSV templates for each import kind with header row and an example line.
4. FR-4 Exports: products (incl. on hand), suppliers, product–supplier links, stock transactions (date range). UTF-8 with BOM for Excel.
5. FR-5 Every committed import is recorded as an `ImportBatch`; stock transactions created by it reference the batch.
6. FR-6 Backup download: `VACUUM INTO` temp file, streamed as `inventory-<yyyyMMdd-HHmm>.db`, then deleted.
7. FR-7 Restore is documented as a manual procedure (stop app, replace file) — not an in-app action in v1.

## Business rules

- Import header matching is case-insensitive; unknown columns ignored; required columns enforced.
- Money columns accept `12.34` (invariant culture); quantities whole numbers.
- Blank cell on update = leave unchanged (not clear), except explicit `""` handling is not supported.
- Imports are audited (F08) as one summary entry per batch, not per row.

## Data model

`ImportBatch` — see architecture §5. CSV column specs in WI-07-1.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Admin/Import` | Choose kind, upload, preview, commit | RequireAdmin |
| `/Admin/Import/Template/{kind}` | Download CSV template | RequireAdmin |
| `/Export` | Export menu + downloads | CanEdit |
| `/Admin/Backup` | Download DB backup | RequireAdmin |

## Out of scope

- Excel (.xlsx) files, scheduled exports, in-app restore, PO import.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-07-1](../work-items/F07-data-import-export/WI-07-1-db-schema.md) | ImportBatch schema, CSV formats | Planned |
| [WI-07-2](../work-items/F07-data-import-export/WI-07-2-api.md) | Import/Export/Backup services | Planned |
| [WI-07-3](../work-items/F07-data-import-export/WI-07-3-frontend.md) | Upload, preview, export, backup pages | Planned |

## Open questions

- Need .xlsx import directly, or is CSV enough?
- Automatic scheduled backups to a folder (e.g. nightly, keep last N)?
