# WI-07-4 — .xlsx import and scheduled backups

- **Feature:** [F07 — Data import/export & backup](../../Features/F07-data-import-export.md)
- **Status:** Blocked
- **Depends on:** WI-07-3
- **Estimate:** M

## Goal

Parked for later (decision: come back to this after F07 ships). Two things users asked for that v1 leaves out:

1. Import `.xlsx` files directly, so a spreadsheet does not have to be saved as CSV first.
2. Automatic backups to a folder (nightly, keep the last N) in addition to the manual download.

## Scope

- In: to be refined when picked up.
- Out: in-app restore.

## Tasks

- [ ] Decide the library for reading `.xlsx` (a new NuGet package must be recorded in `Product/architecture.md` → Decisions; check it works in a self-contained single file, no trimming).
- [ ] Map the first sheet to the same parsers as CSV (same columns, same preview and commit); cap size and rows like CSV.
- [ ] Optionally export `.xlsx` as well.
- [ ] Scheduled backup: hosted service using `BackupService`, settings for on/off, time of day, folder and number to keep; show last run and any failure on `/Admin/Backup`.
- [ ] Tests for both.

## Acceptance criteria

- [ ] To be written when the work item is started.

## Notes

Open questions: which library, whether `.xlsx` export is wanted too, where the default backup folder lives (beside the database?), and what happens when the folder is unavailable.
