# WI-03-1 — StockTransaction ledger

- **Feature:** [F03 — Stock tracking](../../Features/F03-stock-tracking.md)
- **Status:** Done
- **Depends on:** WI-02-1
- **Estimate:** S

## Goal

Immutable stock ledger table with constraints and indexes supporting history and reporting queries.

## Scope

- In: `StockTransaction` entity, `StockTransactionType` enum, configuration, migration.
- Out: PO and import FKs are nullable columns added now but their FK constraints are added in WI-05-1 / WI-07-1.

## Tasks

- [x] `StockTransactionType` enum stored as string (max 20): Receive, Issue, Adjust, Count, Return, Reversal.
- [x] `StockTransaction`: Id, ProductId (FK `Restrict`), Type, QuantityDelta int (≠ 0), QuantityAfter int, UnitCostCents long?,
      Reason (max 100), Reference (max 100), Note (max 500), PurchaseOrderLineId long?, ImportBatchId long?,
      ReversesTransactionId long? (FK self, unique filtered → a transaction can be reversed once), UserId (FK users),
      OccurredAt, CreatedAt.
- [x] Check constraint `QuantityDelta <> 0`.
- [x] Indexes: `(ProductId, Id)`, `OccurredAt`, `Type`, `UserId`, `PurchaseOrderLineId`.
- [x] Navigation `Product.StockTransactions`.
- [x] Migration `AddStockLedger`.

## Acceptance criteria

- [x] Migration applies; a second Reversal of the same transaction violates the unique index.

## Notes

No `UpdatedAt` — rows are never updated. Decided here rather than in WI-03-2.

## Implementation notes

- **Immutability is enforced by the database** (architecture D14): TR_StockTransactions_NoUpdate / _NoDelete triggers abort with Stock transactions are immutable - post a Reversal instead. Hand-written in the migration's Up/Down; StockSchemaTests asserts they exist and fire for both EF and raw SQL.
- Extra CHECK constraints beyond the spec: Type must be a known enum name; Type = 'Reversal' if and only if ReversesTransactionId is set.
- All FKs (product, user, reverses) are Restrict: a product or user with history cannot be deleted.
- CreatedAt is set by TimestampInterceptor through the new IHasCreatedAt marker (insert-only rows have no UpdatedAt and no RowVersion).
- PurchaseOrderLineId and ImportBatchId are plain nullable columns for now; the FKs arrive with WI-05-1 / WI-07-1 (the PO line column is already indexed).
- QuantityAfter is stored as the running balance after the row, so a history page needs no window-function maths; StockService (WI-03-2) is the only writer.
