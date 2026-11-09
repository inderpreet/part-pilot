# WI-11-3 — List, editor, detail

- **Feature:** [F11 — Requisitions](../../Features/F11-requisitions.md)
- **Status:** Done
- **Depends on:** WI-11-2
- **Estimate:** M

## Goal

Any signed-in user can raise a requisition and follow it.

## Scope

- In: `/Requisitions`, `/Requisitions/Edit`, `/Requisitions/{id}`, nav entry.
- Out: approval queue (F12), order links (F13).

## Tasks

- [x] List with Mine / All tabs (All hidden from Viewers), status, requester and date filters, search, status chips.
- [x] Editor: header fields, HTMX line add and remove with part picker showing on hand and on order.
- [x] Detail: lines, status history, Submit / Withdraw / Delete using the F09 confirm pattern.
- [x] Nav: "Requisitions" in the Inventory group for `CanRequest`.
- [x] Settings: requisition number prefix field beside the PO and BOM prefixes.
- [x] Page tests: smoke per page, Viewer can create and cannot open another user's (404), anonymous redirected.

## Acceptance criteria

- [x] A Viewer can create, submit and track a requisition end to end.
- [x] Layout has no horizontal page overflow at 390, 768 and 1440 px, measured in a browser.

## Notes

Use the F09 patterns (architecture D17). Local dates via `Html.LocalDate`.

Overflow measured in headless Edge on the list (Mine and All), new form, editor and detail. The first measurement found the editor 133 px too wide at 390: the `.visually-hidden` header label is absolutely positioned and escaped the table scroller. `.table-wrap` is now `position: relative`, which also covers every other table with such a header.
