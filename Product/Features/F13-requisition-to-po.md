# F13 — Requisition to purchase order

- **Status:** Planned
- **Depends on:** F05, F12
- **Roles:** Admin, Manager and Staff convert. Requesters see the resulting PO number.

## Summary

Turns approved requisition lines into purchase orders. Purchasing picks approved lines from one or many requisitions and puts them on
a **new draft PO** or an **existing draft PO** for the supplier. Each line remembers its order, so the requester can follow it and nothing is ordered twice.

## User stories

- As Staff, I want to see every approved line that has not been ordered, grouped by supplier.
- As Staff, I want to send selected lines to a new draft PO or a draft I already have open.
- As Staff working on a draft PO, I want to add approved lines to it from the PO page.
- As a requester, I want to see which PO my approved parts went on.
- As a Manager, I want to cancel approved lines that are no longer needed.

## Functional requirements

1. FR-1 "To order" list: approved lines with no order link, across requisitions. Shows requisition number, requester, needed-by date, part, approved quantity, on hand, on order, in draft and preferred supplier.
2. FR-2 Lines are grouped by preferred supplier. A line with no preferred supplier goes in "Choose a supplier" and needs one picked before it can be sent.
3. FR-3 For each supplier group the user chooses **new draft PO** or one of that supplier's **Draft** POs. Only `Draft` POs can receive lines (F05 FR-4).
4. FR-4 PO line quantity = approved quantity. A line ordering a part already on the PO adds to that PO line, because a PO has one line per part. Several requisition lines can share one PO line.
5. FR-5 Unit cost defaults as in F05 FR-2 (supplier link, then part cost). Goes through `PurchaseOrderService`, as F10 shortages do.
6. FR-6 A line is converted whole. To order part of it, change the quantity on the draft PO afterwards.
7. FR-7 One DB transaction for the whole action. Each selected line is re-checked inside it.
8. FR-8 Draft PO page gains "Add approved requisition lines", opening the list filtered to that PO's supplier.
9. FR-9 Requisition detail shows the PO number per line (a link). PO detail shows the requisition numbers feeding each line.
10. FR-10 A Manager or Admin can cancel an `Approved` requisition while none of its lines is on a PO.

## Business rules

- **Ordered** and **Partly ordered** are derived for display and filtering, not stored: every approved line, or only some, has an order link. Status stays `Approved`.
- A requisition line links to at most one PO line (unique index), which also stops two users ordering it at once.
- **Release:** removing the line from a draft, deleting the draft or cancelling the PO returns the requisition lines to "To order". The first two happen by cascade; `PurchaseOrderService.CancelAsync` deletes the links in its own transaction.
- A PO quantity lowered by hand does not release anything; the link records the quantity sent at conversion.
- Archived parts and archived suppliers cannot be ordered: the row shows why and is excluded.
- An approved quantity under the supplier's minimum order gets a warning, not a block.
- Once received, lines stay linked. Receiving is unchanged.

## Data model

`RequisitionLineOrder` — see architecture §5. No change to `PurchaseOrderLine`.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Requisitions/ToOrder` | Approved lines not yet ordered, send to PO | CanEdit |
| `/Requisitions/ToOrder?purchaseOrderId={id}` | Same, for one draft PO | CanEdit |
| `/Requisitions/{id}?handler=CancelApproved` (POST) | Cancel an approved requisition | CanApprove |

## Out of scope

- Converting straight to a submitted PO, or submitting the PO automatically.
- Splitting one requisition line across suppliers.
- Notifying the requester when parts arrive.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-13-1](../work-items/F13-requisition-to-po/WI-13-1-db-schema.md) | RequisitionLineOrder schema | Planned |
| [WI-13-2](../work-items/F13-requisition-to-po/WI-13-2-api.md) | Conversion service, release on cancel | Planned |
| [WI-13-3](../work-items/F13-requisition-to-po/WI-13-3-frontend.md) | To-order list, PO and requisition links | Planned |

## Open questions

- Should approved-but-not-ordered quantity appear on the dashboard or in reorder suggestions (F06)? Assumed no for v1.
- ~~Do minimum order quantities need enforcing at conversion?~~ Decided 2026-11-09: warning only.
