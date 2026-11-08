# F03 — Stock tracking

- **Status:** Done
- **Depends on:** F02
- **Roles:** Admin/Staff record movements; Viewer reads history

## Summary

Track every change in on-hand quantity as an immutable ledger entry. Users record goods in, goods out,
adjustments (damage, loss, found) and physical counts. On-hand is cached on the product for fast reads.

## User stories

- As Staff, I want to record goods issued/used so that on-hand stays accurate.
- As Staff, I want to receive stock without a PO (e.g. samples, returns from a job).
- As Staff, I want to adjust stock with a reason (damaged, lost, found, correction).
- As Staff, I want to enter a physical count for one or many products and have the system post the differences.
- As any user, I want to see a product's movement history with who, when and why.
- As Staff, I want to reverse a transaction entered by mistake.

## Functional requirements

1. FR-1 Transaction types: `Receive` (+), `Issue` (−), `Adjust` (±, reason required), `Count` (sets absolute
   quantity; stores the delta), `Return` (+, goods returned to stock), `Reversal` (negates a prior transaction).
   PO receipts (F05) use `Receive` with `PurchaseOrderLineId` set.
2. FR-2 Quick movement form (modal) from product list/detail: type, quantity, reason, reference, note, date (default now, not in future).
3. FR-3 Bulk movement page: multiple lines (product picker + qty), posted as one batch in a single DB transaction.
4. FR-4 Stock count page: filter products (category/bin), enter counted quantities, preview differences, post `Count` transactions only for changed rows.
5. FR-5 Product history: paged list of transactions (date, type, delta, quantity after, user, reason/reference, link to PO).
6. FR-6 Global movements list with filters: date range, type, product, user.
7. FR-7 Reverse a transaction — creates a `Reversal`; a transaction can be reversed only once; reversals cannot be reversed.
8. FR-8 Admin "verify stock" action recomputes on-hand from the ledger and reports/fixes mismatches.

## Business rules

- Input quantities are positive integers; sign derived from type (Adjust takes a signed value).
- Resulting on-hand < 0 is rejected unless setting `Stock.AllowNegative` is true.
- Inactive products cannot receive new movements (except Reversal).
- `QuantityAfter` stored on each row for readable history.
- Backdating (OccurredAt in the past) is allowed; `QuantityAfter` reflects posting order and history is sorted by posting.

## Data model

`StockTransaction` — see architecture §5. Indexes `(ProductId, Id)`, `(OccurredAt)`, `(Type)`.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Stock/Move?productId=&type=` | Quick movement modal | CanEdit |
| `/Stock/Bulk` | Multi-line movement | CanEdit |
| `/Stock/Count` | Stock count | CanEdit |
| `/Stock/Movements` | Global ledger | CanView |
| `/Products/{id}?handler=History` | Product history partial | CanView |
| `/Stock/Reverse/{id}` (POST) | Reverse | CanEdit |
| `/Admin/VerifyStock` | Ledger consistency check | RequireAdmin |

## Out of scope

- Lot/serial/expiry tracking, multiple locations and transfers, costing methods (FIFO/average) — v1 keeps a single product UnitCost.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-03-1](../work-items/F03-stock-tracking/WI-03-1-db-schema.md) | StockTransaction ledger | Done |
| [WI-03-2](../work-items/F03-stock-tracking/WI-03-2-api.md) | StockService | Done |
| [WI-03-3](../work-items/F03-stock-tracking/WI-03-3-frontend.md) | Movements, history, stock count | Done |

## Open questions

- [x] Fixed reason list for Adjust/Issue or free text? Decided: free text with a suggestion list (WI-03-3).
- Should PO receipts update Product.UnitCost (last cost) automatically?
