# WI-05-3 — PO list, editor, receive, print view

- **Feature:** [F05 — Purchase orders & receiving](../../Features/F05-purchase-orders.md)
- **Status:** Planned
- **Depends on:** WI-05-2, WI-04-3
- **Estimate:** L

## Goal

Users create, submit, receive and print POs, and see PO context on product and supplier pages.

## Scope

- In: PO pages, product "Open POs" tab, supplier "Recent POs" section.
- Out: create-from-suggestions UI (WI-06-3).

## Tasks

- [ ] `/PurchaseOrders`: status tabs (Open = Submitted+Partial, Draft, Received, Cancelled, All), supplier/date filters, number search,
      columns: number, supplier, status badge, order/expected date (overdue highlight), total, outstanding.
- [ ] `/PurchaseOrders/Edit/{id?}`: supplier picker (locked after first line), dates, notes; lines table with HTMX add (product picker
      showing supplier-linked products first), inline qty/cost edit, remove; live totals.
- [ ] `/PurchaseOrders/{id}`: header, status badge, lines with ordered/received/outstanding, receipt history (stock transactions), action buttons by status
      (Submit, Cancel, Close short, Delete draft, Receive, Print) with `hx-confirm`.
- [ ] `/PurchaseOrders/{id}/Receive`: tablet-friendly form, per-line qty (default outstanding), "receive all" / "clear all", reference (delivery note #), post.
- [ ] `/PurchaseOrders/{id}/Print`: print-only layout (company, supplier address, lines, totals, notes); auto-opens print dialog.
- [ ] Product detail "Open POs" tab; supplier detail "Recent POs" table.
- [ ] Page tests: draft → submit → partial receive → receive rest; Viewer has no action buttons.

## Acceptance criteria

- [ ] Full lifecycle usable without page reloads for line editing.
- [ ] Print view fits A4/Letter with no nav chrome.

## Notes

—
