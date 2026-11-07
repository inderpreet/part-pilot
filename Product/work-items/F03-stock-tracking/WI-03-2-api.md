# WI-03-2 — StockService

- **Feature:** [F03 — Stock tracking](../../Features/F03-stock-tracking.md)
- **Status:** Planned
- **Depends on:** WI-03-1, WI-02-2
- **Estimate:** L

## Goal

`StockService` is the only way on-hand changes: atomic ledger write + cached quantity update, enforcing all F03 rules.

## Scope

- In: service API, batch posting, counts, reversals, history queries, verify/repair, tests.
- Out: PO receiving orchestration (WI-05-2 calls into this service).

## Tasks

- [ ] `PostAsync(IReadOnlyList<StockMovement> lines, StockContext ctx)` where `StockMovement(ProductId, Type, Quantity, Reason, Reference, Note, OccurredAt, UnitCostCents?, PurchaseOrderLineId?)`.
- [ ] Inside one `BeginTransactionAsync` (SQLite `IMMEDIATE` to avoid write races):
      load products → compute delta per type → check active + negative rule (`Stock.AllowNegative`) → insert rows with `QuantityAfter` → update `QuantityOnHand` → save.
- [ ] Count: delta = counted − current; zero delta lines skipped.
- [ ] Adjust requires non-empty Reason; OccurredAt not in the future (`IClock`).
- [ ] `ReverseAsync(transactionId)`: rejects Reversal types and already-reversed; creates negated row.
- [ ] Queries: `GetHistoryAsync(productId, paging)`, `SearchMovementsAsync(filters)`.
- [ ] `VerifyAsync(fix: bool)` → list of `(ProductId, Cached, Ledger)` mismatches; fix updates cache.
- [ ] Complete TODO from WI-02-2: SKU locked once transactions exist.
- [ ] Decide on DB trigger blocking UPDATE/DELETE on StockTransactions; implement or document.
- [ ] Tests: each type, negative block/allow, batch atomicity (one bad line rolls back all), reversal rules, verify detects tampering,
      concurrent posting from two contexts keeps cache = ledger.

## Acceptance criteria

- [ ] Invariant test: after random sequence of 1,000 operations, every product's cache equals ledger sum.
- [ ] No code path outside `StockService` assigns `QuantityOnHand` (grep check in review).

## Notes

See architecture §6. Return `Result<IReadOnlyList<long>>` with line-indexed errors for UI display.
