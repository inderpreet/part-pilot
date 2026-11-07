# F05 — Purchase orders & receiving

- **Status:** Planned
- **Depends on:** F03, F04
- **Roles:** Admin/Staff create, submit, receive, cancel; Viewer reads

## Summary

Create purchase orders to suppliers, track their status, and receive delivered goods into stock (fully or
partially). Receiving posts `Receive` stock transactions linked to PO lines.

## User stories

- As Staff, I want to create a PO for a supplier, adding products with quantities and costs pre-filled from the supplier link.
- As Staff, I want to submit the PO and print/save it as PDF to send to the supplier.
- As Staff, I want to receive a delivery against a PO, including partial deliveries, so that stock updates automatically.
- As any user, I want to see open POs and what is still outstanding.
- As Staff, I want to cancel a PO that will not be delivered.

## Functional requirements

1. FR-1 PO header: Number (auto `<prefix><yyyy>-<seq 4 digits>`, e.g. `PO-2026-0001`), Supplier, OrderDate,
   ExpectedDate (default OrderDate + max lead time of lines), Notes.
2. FR-2 PO lines: Product (picker filtered to active products, supplier-linked first), QuantityOrdered (> 0),
   UnitCost (defaults to ProductSupplier.UnitCost, else Product.UnitCost). Line and PO totals shown.
3. FR-3 Status machine:
   - `Draft` → `Submitted` (submit; requires at least one line)
   - `Submitted` → `PartiallyReceived` / `Received` (via receiving)
   - `PartiallyReceived` → `Received` (all lines fully received) or closed early via "Close short" → `Received`
   - `Draft` | `Submitted` → `Cancelled`
   - `Draft` deletable; other statuses never deleted.
4. FR-4 Lines editable only in `Draft`. Header notes/expected date editable until `Received`/`Cancelled`.
5. FR-5 Receive screen: per line show ordered, received, outstanding; enter quantity received now (default outstanding);
   post creates one `Receive` transaction per line with quantity > 0, all in one DB transaction.
6. FR-6 Over-receipt (more than outstanding) blocked by default; Admin may allow with confirmation.
7. FR-7 PO list: filter by status, supplier, date range; search by number; outstanding value column.
8. FR-8 Printable PO view (print CSS) with company name, supplier details, lines, totals.
9. FR-9 Product detail shows open PO lines for the product; supplier detail shows recent POs.

## Business rules

- Receiving uses the PO line's UnitCost on the stock transaction.
- On-order quantity (for F06) = Σ outstanding on `Submitted`/`PartiallyReceived` POs.
- PO numbers are sequential per year and never reused (including deleted drafts — gaps acceptable).
- Concurrency: RowVersion on PurchaseOrder; receiving re-checks outstanding inside the transaction.

## Data model

`PurchaseOrder`, `PurchaseOrderLine` — see architecture §5.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/PurchaseOrders` | List | CanView |
| `/PurchaseOrders/{id}` | Detail | CanView |
| `/PurchaseOrders/Edit/{id?}` | Create/edit draft, HTMX line add/remove | CanEdit |
| `/PurchaseOrders/{id}?handler=Submit` / `Cancel` / `CloseShort` / `Delete` (POST) | Transitions | CanEdit |
| `/PurchaseOrders/{id}/Receive` | Receive goods | CanEdit |
| `/PurchaseOrders/{id}/Print` | Printable view | CanView |

## Out of scope

- Emailing POs from the app, supplier invoices/bills matching, returns to supplier, taxes/shipping charges.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-05-1](../work-items/F05-purchase-orders/WI-05-1-db-schema.md) | PurchaseOrder, PurchaseOrderLine schema | Planned |
| [WI-05-2](../work-items/F05-purchase-orders/WI-05-2-api.md) | PurchaseOrderService, receiving | Planned |
| [WI-05-3](../work-items/F05-purchase-orders/WI-05-3-frontend.md) | List, editor, receive, print view | Planned |

## Open questions

- Need tax and shipping lines on the PO?
- Should a PO require approval (e.g. Admin) above a value threshold?
