# WI-08-3 — Audit log viewer, entity history

- **Feature:** [F08 — Audit log](../../Features/F08-audit-log.md)
- **Status:** Planned
- **Depends on:** WI-08-2
- **Estimate:** S

## Goal

Admins search the full audit log; users see change history on detail pages.

## Scope

- In: `/Admin/Audit`, `_AuditHistory` partial on Product/Supplier/PO detail pages.
- Out: —

## Tasks

- [ ] `/Admin/Audit`: filters (date range, user, entity type, action, entity id), paged table (time, user, action, entity link, summary), CSV download.
- [ ] Expandable row showing field diff table (field, old, new) with money/date formatting and friendly field names.
- [ ] `_AuditHistory` partial (`/Audit/Entity/{type}/{id}`) loaded lazily into "History" tabs on Product, Supplier, PO detail.
- [ ] Admin menu entry.
- [ ] Page tests: Staff cannot open `/Admin/Audit`; product history tab shows a cost change.

## Acceptance criteria

- [ ] Changing a product's unit cost then opening its History tab shows old → new value and who changed it.

## Notes

—
