# WI-05-3 — PO list, editor, receive, print view

- **Feature:** [F05 — Purchase orders & receiving](../../Features/F05-purchase-orders.md)
- **Status:** Done
- **Depends on:** WI-05-2, WI-04-3
- **Estimate:** L

## Goal

Users create, submit, receive and print POs, and see PO context on product and supplier pages.

## Scope

- In: PO pages, product "Open POs" tab, supplier "Recent POs" section.
- Out: create-from-suggestions UI (WI-06-3).

## Tasks

- [x] `/PurchaseOrders`: status tabs (Open = Submitted+Partial, Draft, Received, Cancelled, All), supplier/date filters, number search,
      columns: number, supplier, status badge, order/expected date (overdue highlight), total, outstanding.
- [x] `/PurchaseOrders/Edit/{id?}`: supplier picker (locked after first line), dates, notes; lines table with HTMX add (product picker
      showing supplier-linked products first), inline qty/cost edit, remove; live totals.
- [x] `/PurchaseOrders/{id}`: header, status badge, lines with ordered/received/outstanding, receipt history (stock transactions), action buttons by status
      (Submit, Cancel, Close short, Delete draft, Receive, Print) with `hx-confirm`.
- [x] `/PurchaseOrders/{id}/Receive`: tablet-friendly form, per-line qty (default outstanding), "receive all" / "clear all", reference (delivery note #), post.
- [x] `/PurchaseOrders/{id}/Print`: print-only layout (company, supplier address, lines, totals, notes); auto-opens print dialog.
- [x] Product detail "Open POs" tab; supplier detail "Recent POs" table.
- [x] Page tests: draft → submit → partial receive → receive rest; Viewer has no action buttons.

## Acceptance criteria

- [x] Full lifecycle usable without page reloads for line editing.
- [x] Print view fits A4/Letter with no nav chrome.

## Notes

- **Pages** (`Pages/PurchaseOrders/`): `Index` (+ `_OrderResults`), `Detail`, `Edit` (+ `_PoLines`, `_PoProductResults`), `Receive`, `Print`. Shared: `_PoStatusBadge`, `PurchaseOrderDisplay` (labels, badge classes, overdue rule), `_PrintLayout` (no site chrome). The nav entry "Purchase Orders" is now `Available: true`.
- **List:** opens on the *Open* tab (Submitted + Partially received) when no `tab` is given. The status tabs are plain links (a tab change reloads, which keeps the filter form's hidden `tab` field correct); search, supplier and date filters, sort and paging refresh `#results` through htmx. Overdue = open order whose expected date is before today (local date).
- **Editor:** header form (supplier select locked once there are lines, order date locked after submit) and a lines block (`#po-lines`) that every line handler (`AddLine`, `SaveLine`, `RemoveLine`) answers with, so totals are always server-computed. Quantity/cost inputs save on `change`. Failures answer **422** with the block re-rendered (errors listed, add-form values kept). Without htmx the line handlers redirect back with the error as a flash. The product picker is the shared `_ProductPicker` with a new optional `LookupUrl` (here `?handler=Products`, backed by `LookupProductsAsync`: the supplier's products first, price and min order shown, products already on the order disabled). Creating a draft needs only a supplier; `/PurchaseOrders/Edit?supplierId=` pre-selects it (link from the supplier page). A submitted order's editor is header-only (expected date, notes); received/cancelled orders redirect to the detail page.
- **Detail:** actions by status and role (Submit, Close short, Cancel, Delete draft, Receive, Edit, Print) as `hx-post` buttons with `hx-confirm`, also working as plain forms; each ends on the refreshed page (flash via TempData + `HX-Redirect`). The receipts table has a per-receipt **Reverse** (`ReverseReceiptAsync`). Viewers see no action buttons and get 403 on every handler.
- **Receive:** per-line quantity (default outstanding, big touch targets), "Receive all outstanding" / "Clear all" (`site.js`, `data-receive-all` / `data-receive-clear`), delivery-note reference and note. Over-receipt shows an "Allow over-receipt" tick for Admins only; a refused attempt keeps the form and says what to do. Quantities are posted as text so a typo gets a friendly message. The posted lines are re-aligned to the order's own lines, so a missing, duplicated or foreign line id cannot slip through.
- **Print:** `_PrintLayout` (no header/footer/nav) with `body[data-auto-print]`, which `site.js` turns into `window.print()`; A4/Letter-friendly CSS (`.print-sheet`, `@page` margin, rows never split). Draft / Cancelled orders get a stamp. Uses the supplier's own SKU when linked.
- **Beyond the task list:** `StockMovementItem` gained `PurchaseOrderId` / `PurchaseOrderNumber`; the movements/history tables link a receipt to its order (`/PurchaseOrders/{id}#tx-{n}`), where it is reversed. Product page: "Purchase orders" tab is now an **Open purchase orders** section (outstanding per order and the on-order total). Supplier page: **Recent purchase orders** with a "New purchase order" button.
- **Tests:** `Pages/PurchaseOrderPagesTests` (20): full lifecycle through the pages (draft → lines → submit → partial receive → receive the rest), list tabs/filters/sort/overdue/empty states, 422 line validation, picker ordering, header save / lock / conflict, header-only edits after submit, cancel / close short / delete, receipt reversal, over-receipt (Staff refused, Admin with the tick), receive-form typos, print view, supplier and product sections, Viewer and anonymous access, nav link. Total suite: 806.
- **Not verified in a browser:** the htmx behaviours that HttpClient tests cannot exercise (change-triggered row saves, picker dropdown, auto-print dialog). The server-side contract behind each is tested; a manual pass is still worthwhile.
