# WI-05-1 — PurchaseOrder, PurchaseOrderLine schema

- **Feature:** [F05 — Purchase orders & receiving](../../Features/F05-purchase-orders.md)
- **Status:** Done
- **Depends on:** WI-03-1, WI-04-1
- **Estimate:** S

## Goal

PO header/line tables, number sequence storage, and the FK from stock transactions to PO lines.

## Scope

- In: entities, enum, configurations, sequence table, migration.
- Out: service logic (WI-05-2).

## Tasks

- [x] `PurchaseOrderStatus` enum stored as string: Draft, Submitted, PartiallyReceived, Received, Cancelled.
- [x] `PurchaseOrder`: Id, Number (max 30, unique), SupplierId (FK `Restrict`), Status, OrderDate (date), ExpectedDate (date?),
      SubmittedAt?, ClosedAt?, Notes (2000), CreatedById (FK users), RowVersion, timestamps.
- [x] `PurchaseOrderLine`: Id, PurchaseOrderId (FK `Cascade` — only drafts are deletable), ProductId (FK `Restrict`),
      QuantityOrdered int (> 0), QuantityReceived int (>= 0, default 0), UnitCostCents long (>= 0).
- [x] Unique `(PurchaseOrderId, ProductId)` — one line per product per PO.
- [x] `DocumentSequence(Name, Year, NextValue)`, primary key `(Name, Year)`, for PO numbering (row per year).
- [x] Add FK `StockTransaction.PurchaseOrderLineId → PurchaseOrderLine.Id` (`Restrict`).
- [x] Indexes: `PurchaseOrder(Status)`, `(SupplierId, OrderDate)`, `PurchaseOrderLine(ProductId)`.
- [x] Migration `AddPurchaseOrders`.

## Acceptance criteria

- [x] Migration applies; deleting a PO line referenced by a stock transaction fails.

## Notes

`DateOnly` columns stored as TEXT `yyyy-MM-dd` (EF Core default) — sortable.

## Implementation notes

- **Entities / configuration:** `Data/Entities/{PurchaseOrder,PurchaseOrderLine,DocumentSequence}.cs` (`PurchaseOrderStatus` lives with `PurchaseOrder`), `Data/Configurations/PurchaseOrderConfiguration.cs`, migration `AddPurchaseOrders`. `StockTransaction.PurchaseOrderLine` / `PurchaseOrderLine.Receipts` are the new navigations.
- **Deviation: `DocumentSequence` key.** Architecture said `Name PK` with a `Year` column and "a row per year", which cannot hold two years. The key is the pair `(Name, Year)`; PO numbering uses `Name = "PurchaseOrder"`. Architecture §5 updated.
- **Ids:** `PurchaseOrder.Id` is `int`, `PurchaseOrderLine.Id` is `long` (it matches the existing `StockTransaction.PurchaseOrderLineId` column).
- **Constraints:** CHECKs on status (`CK_PurchaseOrders_Status`), `QuantityOrdered > 0`, `QuantityReceived >= 0`, `UnitCostCents >= 0`, `NextValue >= 1`. `Number` is `NOCASE` unique. `QuantityReceived` and `NextValue` are `ValueGeneratedNever` (same EF default-value gotcha as `MinOrderQuantity`). Dates are `DateOnly` stored as `yyyy-MM-dd` text.
- **Deletes:** PO to supplier, product and creator are `Restrict`; PO to lines is `Cascade` (only drafts are deleted, and a draft has no receipts); `StockTransaction` to line is `Restrict`, so a line with receipts cannot be deleted, directly or through its order.
- **Migration gotcha (decision D16):** SQLite cannot add a foreign key in place, so adding the `StockTransactions` FK makes EF rebuild the table, and EF runs rebuilds *after* every other operation of the migration. That drops the immutability triggers of D14 and a `Sql` call in the migration cannot restore them (EF warns about it). `AddPurchaseOrders` therefore writes the rebuild out by hand (create temp, copy, drop, rename, indexes, triggers) for both `Up` and `Down`. The model snapshot is unchanged by this; if `StockTransaction` gets new columns before this runs, the hand-written column lists must follow.
- **Tests:** `Data/PurchaseOrderSchemaTests` (tables/indexes, no pending model changes, defaults, unique number, status CHECK, FKs and restricts, cascade of a draft, line referenced by a receipt, triggers survive, sequences, concurrency, migration on top of the previous schema with ledger rows kept and triggers intact, rollback and re-apply). `StockTestBase.NewPurchaseOrderLineAsync` seeds a real line for tests that post receipts against one.
