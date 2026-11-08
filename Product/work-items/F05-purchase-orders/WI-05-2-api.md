# WI-05-2 — PurchaseOrderService, receiving

- **Feature:** [F05 — Purchase orders & receiving](../../Features/F05-purchase-orders.md)
- **Status:** Done
- **Depends on:** WI-05-1, WI-03-2, WI-04-2
- **Estimate:** L

## Goal

Full PO lifecycle with an enforced state machine, and receiving that posts stock through `StockService` atomically.

## Scope

- In: `PurchaseOrderService`, numbering, transitions, receiving, queries, tests.
- Out: UI (WI-05-3), reorder-suggestion PO creation (WI-06-2 uses `CreateDraftAsync`).

## Tasks

- [x] `NextNumberAsync()` using `DocumentSequence` inside the creating transaction; format `<prefix><yyyy>-<0000>`.
- [x] `CreateDraftAsync(supplierId, lines?)`, `UpdateHeaderAsync`, `AddLineAsync` (default cost: ProductSupplier → Product), `UpdateLineAsync`, `RemoveLineAsync` — lines only in Draft.
- [x] `SubmitAsync` (≥ 1 line; sets SubmittedAt, ExpectedDate default = OrderDate + max lead time), `CancelAsync` (Draft/Submitted),
      `DeleteDraftAsync`, `CloseShortAsync` (PartiallyReceived → Received).
- [x] Central `CanTransition(from, to)` table + tests for every pair.
- [x] `ReceiveAsync(poId, IEnumerable<(lineId, qty)>, reference, note)`: one DB transaction; re-read outstanding; block over-receipt unless `allowOver` and user is Admin;
      call `StockService` with `Receive` movements (UnitCost = line cost, PurchaseOrderLineId set); update QuantityReceived; set status Partially/Received.
- [x] Queries: `SearchAsync` (status, supplier, date range, number), `GetAsync` (with totals, outstanding), `GetOpenLinesForProductAsync`, `GetOnOrderByProductAsync()`.
- [x] Domain events/hooks for F08 (`PoSubmitted`, `PoCancelled`, `PoReceived`).
- [x] Tests: numbering per year and no reuse, state machine, partial then full receive, over-receipt blocked, receipt rollback if stock post fails, concurrency.

## Acceptance criteria

- [x] Receiving 2 of 3 lines fully → PartiallyReceived; remaining line → Received; on-hand increments match.
- [x] Editing lines of a Submitted PO returns an error.

## Notes

Receiving must go through `StockService` — do not write StockTransactions directly.

## Implementation notes

- **Files:** `Services/PurchaseOrderModels.cs` (rules, queries, records, events), `Services/PurchaseOrderService.cs`, `Services/PurchaseOrderServiceCollectionExtensions.cs` (`AddPurchaseOrders()`, registered in `Program.cs`).
- **State machine:** `PurchaseOrderRules.CanTransition` is the single table (tested for all 25 pairs) plus per-action predicates (`CanEditLines`, `CanReceive`, `CanCancel`, ...) the UI reuses. Beyond the spec's arrows it allows `Received → PartiallyReceived/Submitted` and `PartiallyReceived → Submitted`, which only happen when a receipt is reversed (see below).
- **Numbering:** `NextNumberAsync(year)` bumps `DocumentSequence(Name = "PurchaseOrder", Year)` with one `UPDATE` (so the write lock is held before the value is read) inside the creating transaction. The year is the **order date's** year (a PO dated 2027-01-02 is `PO-2027-0001`). Numbers of rolled-back or deleted orders are not reissued once committed; a rolled-back transaction hands its number back.
- **Identity of the actor:** `PurchaseOrderActor(UserId, IsAdmin)`. Over-receipt needs `allowOverReceipt` **and** `IsAdmin`; otherwise `po.over_receipt` / `po.over_receipt_forbidden`.
- **Costs:** a line without an explicit cost takes the supplier link's price when it is above zero (a link created without a price has `0`, which means "not entered"), else the product's cost. Costs are capped at `MaxUnitCostCents` (1,000,000.00) so a line total fits a `long`.
- **Concurrency:** line operations and transitions run in a transaction and re-read the order inside it. Line edits deliberately do **not** bump the order's `RowVersion` (same reasoning as D15: otherwise an open header form would conflict with a line added a moment earlier). The header edit, and the optional `rowVersion` argument of Submit/Cancel/Delete/CloseShort/Receive, are the stale-page guards. Receiving re-reads outstanding quantities inside the transaction, so two receive screens cannot over-receive.
- **Receiving** calls `IStockService.PostAsync` inside the order's transaction (it joins it), so a stock refusal (archived product, overflow) leaves nothing behind; stock errors are re-mapped from stock-line paths to the receipt form's `Lines[i].Quantity` (input index). The ledger row gets `Reason = "Received on <number>"` and `Reference = <delivery note> ?? <number>`.
- **Resolved open question (reversing a receipt):** `IStockService.ReverseAsync` now **refuses** rows with a `PurchaseOrderLineId` (`stock.reverse_order_receipt`) and `StockMovementItem.CanReverse` is false for them, so the generic Reverse button disappears for receipts. `IPurchaseOrderService.ReverseReceiptAsync` does it instead: in one transaction it calls the new `IStockService.ReverseOrderReceiptAsync`, lowers the line's `QuantityReceived` and re-derives the status. A *received* order is reopened (`Received → PartiallyReceived/Submitted`, `ClosedAt` cleared), including one that was closed short. Decision recorded in the feature spec.
- **Events:** `IPurchaseOrderEvents` / `PurchaseOrderEventPublisher` (Created, Submitted, Cancelled, DraftDeleted, Received, ClosedShort, ReceiptReversed), published after commit; a failing handler is logged, never thrown (same pattern as `IUserEvents`). F08 registers handlers.
- **Queries beyond the spec** for WI-05-3 / F06: `GetReceiptsAsync` (receipts of an order with `CanReverse`), `GetRecentForSupplierAsync`, `LookupProductsAsync` (editor product picker: the supplier's linked products first, default cost, supplier SKU, "already on order").
- **"Outstanding value"** (list column, detail): `Σ max(0, ordered − received) × cost` for Submitted/PartiallyReceived orders, `0` otherwise (a closed-short remainder is not outstanding).
- **Tests:** `Services/PurchaseOrderServiceTests` (101 cases: transition table, numbering, drafts, header, lines, every transition, receiving incl. rollback and over-receipt, reversing receipts, list filters/sorts, product/supplier queries, events), `Services/PurchaseOrderConcurrencyTests` (file DB: parallel numbering, parallel receiving, submit vs cancel), plus new stock tests for the two reverse entry points.
