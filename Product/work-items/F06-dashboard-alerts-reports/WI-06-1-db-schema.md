# WI-06-1 — Query indexes, reporting queries

- **Feature:** [F06 — Dashboard, alerts & reports](../../Features/F06-dashboard-alerts-reports.md)
- **Status:** Done
- **Depends on:** WI-05-1
- **Estimate:** S

## Goal

No new tables; the schema supports dashboard/report queries efficiently and the core read queries are defined and benchmarked.

## Scope

- In: indexes, keyless read models / SQL for on-order and low-stock, performance seed data, query benchmarks.
- Out: service API (WI-06-2).

## Tasks

- [x] Define on-order aggregate query: Σ(QuantityOrdered − QuantityReceived) per product for Submitted/PartiallyReceived POs.
- [x] Define low-stock query joining products + on-order + preferred supplier (LINQ; raw SQL only if LINQ translation is poor).
- [x] Add indexes found necessary, e.g. `Product(IsActive, ReorderPoint)`, `StockTransaction(Type, OccurredAt)`, `PurchaseOrder(Status, ExpectedDate)`.
- [x] `DevSeeder` (Development only, `--seed-demo`): 10k products, 200 suppliers, 2k POs, 1M transactions.
- [x] Measure each dashboard/report query on seeded DB (`EXPLAIN QUERY PLAN`), record timings in Notes.
- [x] Migration `AddReportingIndexes` (if indexes added).

## Acceptance criteria

- [x] Each dashboard query < 100 ms and each report first page < 300 ms on seeded DB.

## Notes

Only the indexes the measurements asked for were added. `Product(IsActive, ReorderPoint)` and `PurchaseOrder(Status, ExpectedDate)` from the task list were **not** needed: every product and purchase-order query is already under 10 ms on the full data set with the F00-F05 indexes, so adding them would only cost writes.

Two ledger queries missed the budget with the existing indexes (both read `StockTransactions` through `IX_StockTransactions_Type`, about 600k Issue rows, and then fetched table rows):

| Query | Before | After |
|---|---:|---:|
| Movements report, 30 days, one type (rows + count) | 289 ms | 1.2 ms |
| Usage report, top consumers over 90 days | 307-878 ms | 55 ms |
| Dead stock (no movement for 90 days, rows + count) | 140 ms | 34 ms |

Fix: `IX_StockTransactions_Type` is replaced by `(Type, OccurredAt, ProductId, QuantityDelta)` (the extra columns make the usage aggregate a covering-index scan) and `(ProductId, OccurredAt)` is added (dead stock becomes one index seek per product). Plain `CREATE`/`DROP INDEX`: `StockTransactions` is not rebuilt, so the D14 immutability triggers are untouched.

Timings on the demo data set (10,000 products, 200 suppliers, 2,000 POs, 1,000,000 ledger rows; median of 5 runs after a warm-up, SQLite file database, no `ANALYZE`, as the app runs it):

| Query | Kind | Median ms |
|---|---|---:|
| Nav badge / tile: low-stock count | dashboard | 5.3 |
| Tile: active SKUs | dashboard | 0.3 |
| Tile: total stock value | dashboard | 0.7 |
| Tile: out-of-stock count | dashboard | 0.4 |
| Tile: open POs (count) | dashboard | 0.1 |
| Tile: open POs (value) | dashboard | 0.5 |
| Tile: overdue POs | dashboard | 0.2 |
| List: top 10 low-stock | dashboard | 6.0 |
| List: last 10 movements | dashboard | 0.2 |
| List: open POs due this week | dashboard | 0.5 |
| On-order aggregate | dashboard | 1.7 |
| Low-stock page (50 rows + count) | report page | 6.7 |
| Valuation report (50 rows + count + total) | report page | 1.1 |
| Valuation by category | report page | 2.2 |
| Movements report, 30 days (50 rows + count) | report page | 3.0 |
| Movements report, 30 days, Issue only | report page | 1.2 |
| Movements report, one product | report page | 0.3 |
| Usage report, top consumers, 90 days | report page | 55.4 |
| PO summary by supplier, 12 months | report page | 3.8 |
| PO summary list in a date range | report page | 0.9 |
| Dead stock, 90 days | report page | 33.8 |

The query *shapes* for the dashboard tiles and reports are in `tests/InventoryTracker.Tests/Data/ReportingBenchmarks.cs`; WI-06-2 builds the services on them and should re-run the benchmark through the services (`$env:INVENTORY_BENCH = 1; dotnet test --filter ReportingBenchmarks`; it is skipped otherwise, and writes the table plus every query plan to `%TEMP%\f06-benchmark.md`).

Decisions worth carrying into WI-06-2:

- "Open POs value" in the benchmark is the order total (Σ ordered × unit cost, as the PO page shows it). If the dashboard should show only what is still to arrive, use `QuantityOrdered - QuantityReceived`; same cost.
- `LowStockRow` has the preferred supplier's id, name, minimum order quantity and price (or nulls). It does not decide whether an archived preferred supplier counts: WI-06-2 should treat it as "no preferred supplier" when it creates draft POs (a PO cannot be raised against an archived supplier).
- Row types are classes with `init` properties, not positional records: EF cannot filter, sort or page after a constructor projection.

## As built

- `Services/ReportingQueries.cs`: `OnOrder(db)` and `LowStock(db)` extension queries (composable `IQueryable`s) with `OnOrderRow` / `LowStockRow`. Low stock is "active, `ReorderPoint > 0`, on hand + on order <= reorder point"; on order counts Submitted and PartiallyReceived orders only.
- `Data/DevSeeder.cs`: deterministic (fixed random seed, injectable clock), idempotent (`DEMO-` prefix), writes the ledger with plain SQL in one transaction (about 22 s for 1M rows) and keeps the ledger invariants (`QuantityAfter` in posting order, never negative, `QuantityOnHand` = ledger sum, PO line `QuantityReceived` = its receipts). Needs an existing account to attribute rows to; says so and does nothing otherwise.
- `--seed-demo` flag (`CliOptions.SeedDemo`, `Program.cs`, `scripts/dev.ps1 -SeedDemo`); ignored with a warning outside Development.
- Migration `AddReportingIndexes`; `StockConfiguration` updated; `StockSchemaTests` expects the new index names.
- Tests: `Data/ReportingQueriesTests.cs` (15, including the F06 acceptance example), `Data/DevSeederTests.cs` (8), `Data/ReportingIndexSchemaTests.cs` (6: fresh DB, query plans, upgrade over the previous schema with data and ledger triggers, rollback and re-apply), `Hosting/HostSettingsTests` flag test, and the opt-in `Data/ReportingBenchmarks.cs`.
- Architecture: D20, the `StockTransaction` index line, `--seed-demo` in the configuration table; README configuration table and Development section.
