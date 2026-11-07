# WI-05-1 — PurchaseOrder, PurchaseOrderLine schema

- **Feature:** [F05 — Purchase orders & receiving](../../Features/F05-purchase-orders.md)
- **Status:** Planned
- **Depends on:** WI-03-1, WI-04-1
- **Estimate:** S

## Goal

PO header/line tables, number sequence storage, and the FK from stock transactions to PO lines.

## Scope

- In: entities, enum, configurations, sequence table, migration.
- Out: service logic (WI-05-2).

## Tasks

- [ ] `PurchaseOrderStatus` enum stored as string: Draft, Submitted, PartiallyReceived, Received, Cancelled.
- [ ] `PurchaseOrder`: Id, Number (max 30, unique), SupplierId (FK `Restrict`), Status, OrderDate (date), ExpectedDate (date?),
      SubmittedAt?, ClosedAt?, Notes (2000), CreatedById (FK users), RowVersion, timestamps.
- [ ] `PurchaseOrderLine`: Id, PurchaseOrderId (FK `Cascade` — only drafts are deletable), ProductId (FK `Restrict`),
      QuantityOrdered int (> 0), QuantityReceived int (>= 0, default 0), UnitCostCents long (>= 0).
- [ ] Unique `(PurchaseOrderId, ProductId)` — one line per product per PO.
- [ ] `DocumentSequence(Name PK, Year, NextValue)` for PO numbering (row per year).
- [ ] Add FK `StockTransaction.PurchaseOrderLineId → PurchaseOrderLine.Id` (`Restrict`).
- [ ] Indexes: `PurchaseOrder(Status)`, `(SupplierId, OrderDate)`, `PurchaseOrderLine(ProductId)`.
- [ ] Migration `AddPurchaseOrders`.

## Acceptance criteria

- [ ] Migration applies; deleting a PO line referenced by a stock transaction fails.

## Notes

`DateOnly` columns stored as TEXT `yyyy-MM-dd` (EF Core default) — sortable.
