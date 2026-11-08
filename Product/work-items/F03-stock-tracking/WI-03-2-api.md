# WI-03-2 — StockService

- **Feature:** [F03 — Stock tracking](../../Features/F03-stock-tracking.md)
- **Status:** Done
- **Depends on:** WI-03-1, WI-02-2
- **Estimate:** L

## Goal

`StockService` is the only way on-hand changes: atomic ledger write + cached quantity update, enforcing all F03 rules.

## Scope

- In: service API, batch posting, counts, reversals, history queries, verify/repair, tests.
- Out: PO receiving orchestration (WI-05-2 calls into this service).

## Tasks

- [x] `PostAsync(IReadOnlyList<StockMovement> lines, StockContext ctx)` where `StockMovement(ProductId, Type, Quantity, Reason, Reference, Note, OccurredAt, UnitCostCents?, PurchaseOrderLineId?, ImportBatchId?)`.
- [x] Inside one `BeginTransactionAsync` (SQLite `IMMEDIATE` to avoid write races):
      load products → compute delta per type → check active + negative rule (`Stock.AllowNegative`) → insert rows with `QuantityAfter` → update `QuantityOnHand` → save.
- [x] Count: delta = counted − current; zero delta lines skipped.
- [x] Adjust requires non-empty Reason; OccurredAt not in the future (`IClock`).
- [x] `ReverseAsync(transactionId)`: rejects Reversal types and already-reversed; creates negated row.
- [x] Queries: `GetHistoryAsync(productId, paging)`, `SearchMovementsAsync(filters)`.
- [x] `VerifyAsync(fix: bool)` → list of `(ProductId, Cached, Ledger)` mismatches; fix updates cache.
- [x] Complete TODO from WI-02-2: SKU locked once transactions exist.
- [x] DB trigger blocking UPDATE/DELETE on StockTransactions: implemented in WI-03-1 (D14).
- [x] Tests: each type, negative block/allow, batch atomicity (one bad line rolls back all), reversal rules, verify detects tampering,
      concurrent posting from two contexts keeps cache = ledger.

## Acceptance criteria

- [x] Invariant test: after random sequence of 1,000 operations, every product's cache equals ledger sum.
- [x] No code path outside `StockService` assigns `QuantityOnHand` (grep check in review).

## Notes

See architecture §6. Return `Result<IReadOnlyList<long>>` with line-indexed errors for UI display.

## Implementation notes

- **API** (Services/StockService.cs, models in StockModels.cs, DI via `AddStock()`): `PostAsync`, `ReverseAsync`, `GetAsync`, `GetHistoryAsync`, `SearchMovementsAsync`, `PreviewCountAsync` (for the count page's preview), `VerifyAsync(fix)`. `StockContext(UserId)` is passed explicitly rather than read from the request, so PO receiving and imports can post too. `PostAsync` returns `Result<IReadOnlyList<PostedMovement>>`: one entry per input line (a Count that matches has `TransactionId = null`).
- **Errors are line-indexed** using model-binding paths (`Lines[2].Quantity`); `StockFields.Unindexed` strips the prefix for the single-line modal. All lines are validated and every problem is returned before anything is written.
- **Transactions:** its own `BeginTransactionAsync` (Microsoft.Data.Sqlite turns that into `BEGIN IMMEDIATE`, so the write lock is taken before the quantities are read); an already-open transaction on the context is reused and left for the caller to commit (PO receiving).
- **Cache write** is a direct `ExecuteUpdate` with an "is still the value I read" guard, so a stock movement does **not** bump `Product.RowVersion` / `UpdatedAt` (architecture D15). The guard failing aborts the whole post (only possible if someone edits the DB by hand mid-flight).
- **Rules decided here:** a movement that *reduces* stock below zero is refused unless `Stock.AllowNegative`; one that raises a negative balance is always allowed. Quantities (and on-hand) are capped at 1,000,000,000. `OccurredAt` up to 5 minutes ahead (LAN clock skew) is clamped to now, further ahead is rejected; local/unspecified kinds are converted to UTC. Reversal is allowed on archived products, copies unit cost, reference and `PurchaseOrderLineId` from the original, and is subject to the negative-stock rule (reversing a receipt that has since been used is refused).
- **SKU lock:** `ProductService.IsSkuLockedAsync` now checks the ledger (WI-02-2 TODO closed).
- **Verified:** concurrent posting on a real file DB (8 connections × 25 mixed operations; 10 racing issues of the last unit -> exactly one wins; 6 racing reversals -> exactly one), a seeded 1,000-operation random test, and `StockInvariantSourceTests` which fails if any production file other than `StockService` writes `QuantityOnHand`.
- **Open for F05:** reversing a PO receipt must also lower the PO line's `QuantityReceived`; the generic reverse action should be disabled or routed through `PurchaseOrderService` for rows with a `PurchaseOrderLineId`.