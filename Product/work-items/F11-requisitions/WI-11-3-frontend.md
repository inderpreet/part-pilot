# WI-11-3 — List, editor, detail

- **Feature:** [F11 — Requisitions](../../Features/F11-requisitions.md)
- **Status:** Planned
- **Depends on:** WI-11-2
- **Estimate:** M

## Goal

Any signed-in user can raise a requisition and follow it.

## Scope

- In: `/Requisitions`, `/Requisitions/Edit`, `/Requisitions/{id}`, nav entry.
- Out: approval queue (F12), order links (F13).

## Tasks

- [ ] List with Mine / All tabs (All hidden from Viewers), status, requester and date filters, search, status chips.
- [ ] Editor: header fields, HTMX line add and remove with part picker showing on hand and on order.
- [ ] Detail: lines, status history, Submit / Withdraw / Delete using the F09 confirm pattern.
- [ ] Nav: "Requisitions" in the Inventory group for `CanRequest`.
- [ ] Settings: requisition number prefix field beside the PO and BOM prefixes.
- [ ] Page tests: smoke per page, Viewer can create and cannot open another user's (404), anonymous redirected.

## Acceptance criteria

- [ ] A Viewer can create, submit and track a requisition end to end.
- [ ] Layout has no horizontal page overflow at 390, 768 and 1440 px, measured in a browser.

## Notes

Use the F09 patterns (architecture D17). Local dates via `Html.LocalDate`.
