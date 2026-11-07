# WI-06-1 — Query indexes, reporting queries

- **Feature:** [F06 — Dashboard, alerts & reports](../../Features/F06-dashboard-alerts-reports.md)
- **Status:** Planned
- **Depends on:** WI-05-1
- **Estimate:** S

## Goal

No new tables; the schema supports dashboard/report queries efficiently and the core read queries are defined and benchmarked.

## Scope

- In: indexes, keyless read models / SQL for on-order and low-stock, performance seed data, query benchmarks.
- Out: service API (WI-06-2).

## Tasks

- [ ] Define on-order aggregate query: Σ(QuantityOrdered − QuantityReceived) per product for Submitted/PartiallyReceived POs.
- [ ] Define low-stock query joining products + on-order + preferred supplier (LINQ; raw SQL only if LINQ translation is poor).
- [ ] Add indexes found necessary, e.g. `Product(IsActive, ReorderPoint)`, `StockTransaction(Type, OccurredAt)`, `PurchaseOrder(Status, ExpectedDate)`.
- [ ] `DevSeeder` (Development only, `--seed-demo`): 10k products, 200 suppliers, 2k POs, 1M transactions.
- [ ] Measure each dashboard/report query on seeded DB (`EXPLAIN QUERY PLAN`), record timings in Notes.
- [ ] Migration `AddReportingIndexes` (if indexes added).

## Acceptance criteria

- [ ] Each dashboard query < 100 ms and each report first page < 300 ms on seeded DB.

## Notes

Timings: _fill in_.
