# WI-13-3 — To-order list, PO and requisition links

- **Feature:** [F13 — Requisition to purchase order](../../Features/F13-requisition-to-po.md)
- **Status:** Done
- **Depends on:** WI-13-2, WI-12-3
- **Estimate:** M

## Goal

Purchasing turns approved lines into POs from one screen, and both sides show the link.

## Scope

- In: `/Requisitions/ToOrder`, requisition and PO detail links, cancel approved.
- Out: changes to receiving.

## Tasks

- [x] To-order list grouped by preferred supplier, with select per line and per group, and a supplier picker for lines with none.
- [x] Per group: "New draft PO" or a choice of that supplier's drafts.
- [x] Result summary linking to each PO. Row errors stay on the screen with their reason.
- [x] Draft PO page: "Add approved requisition lines" opens the list with `purchaseOrderId`.
- [x] Requisition detail: PO number per line. PO detail: requisition numbers per line.
- [x] Status chips for Ordered and Partly ordered; list filter.
- [x] Cancel approved action with the F09 confirm pattern.
- [x] Page tests: Staff converts to new and existing draft, Viewer 403, requester sees the PO number, nav entry hidden from Viewers.

## Acceptance criteria

- [x] Full path works: raise, approve part of it, convert to a draft, submit the PO.
- [x] Layout has no horizontal page overflow at 390, 768 and 1440 px, measured in a browser.

## Notes

Show the same in-stock / on-order / in-draft figures as the BOM page (F10) so users can avoid ordering twice.

Overflow measured in headless Edge on the to-order list (all and for one draft), ordered requisition, PO detail, filtered list and the cancel menu. The first run found the cancel menu 249 px off the left edge at 390 px when it is the only action: `.menu-pop` now opens rightward on phones when the menu is the first action. The form reads `Targets[...]` and `SupplierFor[...]` by hand, because model binding a dictionary with no matching fields falls back to every form field and failed on the antiforgery token.
