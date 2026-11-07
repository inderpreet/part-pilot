# WI-05-2 — PurchaseOrderService, receiving

- **Feature:** [F05 — Purchase orders & receiving](../../Features/F05-purchase-orders.md)
- **Status:** Planned
- **Depends on:** WI-05-1, WI-03-2, WI-04-2
- **Estimate:** L

## Goal

Full PO lifecycle with an enforced state machine, and receiving that posts stock through `StockService` atomically.

## Scope

- In: `PurchaseOrderService`, numbering, transitions, receiving, queries, tests.
- Out: UI (WI-05-3), reorder-suggestion PO creation (WI-06-2 uses `CreateDraftAsync`).

## Tasks

- [ ] `NextNumberAsync()` using `DocumentSequence` inside the creating transaction; format `<prefix><yyyy>-<0000>`.
- [ ] `CreateDraftAsync(supplierId, lines?)`, `UpdateHeaderAsync`, `AddLineAsync` (default cost: ProductSupplier → Product), `UpdateLineAsync`, `RemoveLineAsync` — lines only in Draft.
- [ ] `SubmitAsync` (≥ 1 line; sets SubmittedAt, ExpectedDate default = OrderDate + max lead time), `CancelAsync` (Draft/Submitted),
      `DeleteDraftAsync`, `CloseShortAsync` (PartiallyReceived → Received).
- [ ] Central `CanTransition(from, to)` table + tests for every pair.
- [ ] `ReceiveAsync(poId, IEnumerable<(lineId, qty)>, reference, note)`: one DB transaction; re-read outstanding; block over-receipt unless `allowOver` and user is Admin;
      call `StockService` with `Receive` movements (UnitCost = line cost, PurchaseOrderLineId set); update QuantityReceived; set status Partially/Received.
- [ ] Queries: `SearchAsync` (status, supplier, date range, number), `GetAsync` (with totals, outstanding), `GetOpenLinesForProductAsync`, `GetOnOrderByProductAsync()`.
- [ ] Domain events/hooks for F08 (`PoSubmitted`, `PoCancelled`, `PoReceived`).
- [ ] Tests: numbering per year and no reuse, state machine, partial then full receive, over-receipt blocked, receipt rollback if stock post fails, concurrency.

## Acceptance criteria

- [ ] Receiving 2 of 3 lines fully → PartiallyReceived; remaining line → Received; on-hand increments match.
- [ ] Editing lines of a Submitted PO returns an error.

## Notes

Receiving must go through `StockService` — do not write StockTransactions directly.
