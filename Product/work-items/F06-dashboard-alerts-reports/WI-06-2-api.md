# WI-06-2 — ReportService, reorder suggestions

- **Feature:** [F06 — Dashboard, alerts & reports](../../Features/F06-dashboard-alerts-reports.md)
- **Status:** Done
- **Depends on:** WI-06-1, WI-05-2
- **Estimate:** M

## Goal

Services for dashboard metrics, low-stock/reorder suggestions, standard reports and their CSV output.

## Scope

- In: `DashboardService`, `ReorderService`, `ReportService`, CSV writer helper, tests.
- Out: UI (WI-06-3).

## Tasks

- [x] `DashboardService.GetAsync()`: tiles (FR-1) + lists (FR-2).
- [x] `ILowStockCounter` with a 60 s cache for the nav badge; invalidated after stock posts / PO changes.
- [x] `ReorderService.GetSuggestionsAsync(filter)`: on hand, on order, reorder point, suggested qty (FR-5 incl. MinOrderQuantity rounding), preferred supplier.
- [x] `ReorderService.CreateDraftPosAsync(productIds/quantities)`: group by preferred supplier → `PurchaseOrderService.CreateDraftAsync`; return created POs + skipped items.
- [x] Update `ProductService` low-stock flag to include on-order (replace WI-02-2 interim rule).
- [x] `ReportService`: Valuation, Movements, Usage, PurchaseOrderSummary, DeadStock — each `(filter) → report with PagedResult<Row> + totals` and `Write…CsvAsync(filter, Stream)`.
- [x] `CsvExport` helper (CsvHelper, UTF-8 BOM, invariant numbers, money as decimal).
- [x] Tests: suggestion math (incl. rounding, on-order offset), grouping by supplier, report filters, CSV headers.

## Acceptance criteria

- [x] Product with on hand 5, on order 10, reorder point 12 is **not** low stock; with on order 0 it is, suggested qty = max(ReorderQuantity, 7).
- [x] CSV totals match on-screen totals for same filter.

## Notes

- **CsvHelper 33.1.0, not 34.** 34.0.0 is a rewrite with a different API (no `CsvHelper.Configuration`, `CsvWriter` replaced by record writers); 33.x is the classic API everyone documents. Recorded in architecture D21.
- **No `IMemoryCache`.** The badge count is a small singleton (`LowStockCounter`) with a 60 s age limit. It also compares a change counter (`ReportingChangeSignal`) that a `SaveChangesInterceptor` bumps whenever a save touches parts, stock transactions or purchase orders, so a movement shows in the badge on the next page rather than up to a minute later. The interceptor sees every write path (services, imports, PO receipts), so nothing has to remember to call `Invalidate()`; `Invalidate()` exists for the rare out-of-band change.
- **Dashboard tiles are live, not cached.** Each is a few milliseconds (WI-06-1 benchmark); only the nav badge (rendered on every page) is cached.
- **Usage report counts `Issue` movements only.** A reversal of an issue is a `Return`/`Reverse` row and is *not* netted off, so usage can be slightly overstated if issues were reversed. Recorded under the feature's open questions.
- **Archived preferred supplier = cannot order.** The suggestion is still shown (`CanOrder = false`); `CreateDraftPosAsync` reports the item as skipped with the reason. Quantities below 1, unknown or archived parts, and parts without a preferred supplier are skipped the same way. A supplier whose draft cannot be created (for example a quantity over the maximum) has its items reported as skipped; other suppliers' drafts are still created.
- **Suggested quantity is never below 1** (a part exactly at its reorder point with no reorder quantity still needs something), then raised to the supplier's minimum order quantity.
- **"Open PO value"** on the tile is the order total (Σ ordered × unit cost); the "due this week" list shows only what is still to arrive.
- Reports: Valuation and Dead stock exclude archived parts (Valuation can include them); PO summary totals "Outstanding" for open orders only.

## As built

- `Services/DashboardService.cs` (`IDashboardService`): `GetAsync` (tiles + lists), `GetTilesAsync`, `GetLowStockAsync`, `GetRecentMovementsAsync`, `GetDueThisWeekAsync` (open orders expected today..today+7).
- `Services/LowStockCounter.cs` (`ILowStockCounter`, singleton) and `Data/Interceptors/ReportingChangeInterceptor.cs` (`ReportingChangeSignal`, registered with the other interceptors).
- `Services/ReorderService.cs` (`IReorderService`: `GetSuggestionsAsync(ReorderFilter)`, `CreateDraftPosAsync(requests, actor)`), `ReorderMath.SuggestedQuantity`.
- `Services/ReportService.cs` (`IReportService`): `GetValuationAsync`, `GetMovementsAsync`, `GetUsageAsync`, `GetPurchaseOrdersAsync`, `GetDeadStockAsync` and a `Write…CsvAsync` for each; screen and CSV use the same filter and ordering (CSV is all rows, not just the page).
- `Services/CsvExport.cs`: `CsvColumn.Text/Int/Money/Utc/Date`, `CsvExport.WriteAsync` (UTF-8 BOM, CRLF, invariant culture, formula-injection guard on text cells).
- `Services/ReportingModels.cs`: filters, rows, totals. `Services/ReportingServiceCollectionExtensions.cs`: `AddReporting()`.
- `ProductService`: list and detail `IsLowStock` now count what is on order.
- Tests: `ReorderServiceTests`, `DashboardServiceTests`, `LowStockCounterTests` (cache age, change signal, failed saves), `ReportServiceTests`, `CsvExportTests`, `ProductLowStockTests`.
