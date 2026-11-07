# WI-03-1 — StockTransaction ledger

- **Feature:** [F03 — Stock tracking](../../Features/F03-stock-tracking.md)
- **Status:** Planned
- **Depends on:** WI-02-1
- **Estimate:** S

## Goal

Immutable stock ledger table with constraints and indexes supporting history and reporting queries.

## Scope

- In: `StockTransaction` entity, `StockTransactionType` enum, configuration, migration.
- Out: PO and import FKs are nullable columns added now but their FK constraints are added in WI-05-1 / WI-07-1.

## Tasks

- [ ] `StockTransactionType` enum stored as string (max 20): Receive, Issue, Adjust, Count, Return, Reversal.
- [ ] `StockTransaction`: Id, ProductId (FK `Restrict`), Type, QuantityDelta int (≠ 0), QuantityAfter int, UnitCostCents long?,
      Reason (max 100), Reference (max 100), Note (max 500), PurchaseOrderLineId long?, ImportBatchId long?,
      ReversesTransactionId long? (FK self, unique filtered → a transaction can be reversed once), UserId (FK users),
      OccurredAt, CreatedAt.
- [ ] Check constraint `QuantityDelta <> 0`.
- [ ] Indexes: `(ProductId, Id)`, `OccurredAt`, `Type`, `UserId`, `PurchaseOrderLineId`.
- [ ] Navigation `Product.StockTransactions`.
- [ ] Migration `AddStockLedger`.

## Acceptance criteria

- [ ] Migration applies; a second Reversal of the same transaction violates the unique index.

## Notes

No `UpdatedAt` — rows are never updated. Consider a SQLite trigger blocking UPDATE/DELETE on the table (decide in WI-03-2; record in architecture).
