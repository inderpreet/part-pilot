# WI-06-2 — ReportService, reorder suggestions

- **Feature:** [F06 — Dashboard, alerts & reports](../../Features/F06-dashboard-alerts-reports.md)
- **Status:** Planned
- **Depends on:** WI-06-1, WI-05-2
- **Estimate:** M

## Goal

Services for dashboard metrics, low-stock/reorder suggestions, standard reports and their CSV output.

## Scope

- In: `DashboardService`, `ReorderService`, `ReportService`, CSV writer helper, tests.
- Out: UI (WI-06-3).

## Tasks

- [ ] `DashboardService.GetAsync()`: tiles (FR-1) + lists (FR-2).
- [ ] `ILowStockCounter` with 60 s `IMemoryCache` for nav badge; invalidated after stock posts / PO changes.
- [ ] `ReorderService.GetSuggestionsAsync(filter)`: on hand, on order, reorder point, suggested qty (FR-5 incl. MinOrderQuantity rounding), preferred supplier.
- [ ] `ReorderService.CreateDraftPosAsync(productIds/quantities)`: group by preferred supplier → `PurchaseOrderService.CreateDraftAsync`; return created POs + skipped items.
- [ ] Update `ProductService` low-stock flag to include on-order (replace WI-02-2 interim rule).
- [ ] `ReportService`: Valuation, Movements, Usage, PurchaseOrderSummary, DeadStock — each `(filter) → PagedResult<Row>` and `StreamCsvAsync(filter, Stream)`.
- [ ] `CsvExport` helper (CsvHelper, UTF-8 BOM, invariant numbers, money as decimal).
- [ ] Tests: suggestion math (incl. rounding, on-order offset), grouping by supplier, report filters, CSV headers.

## Acceptance criteria

- [ ] Product with on hand 5, on order 10, reorder point 12 is **not** low stock; with on order 0 it is, suggested qty = max(ReorderQuantity, 7).
- [ ] CSV totals match on-screen totals for same filter.

## Notes

—
