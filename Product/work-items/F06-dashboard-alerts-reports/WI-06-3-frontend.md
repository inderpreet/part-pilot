# WI-06-3 — Dashboard, low-stock, reports pages

- **Feature:** [F06 — Dashboard, alerts & reports](../../Features/F06-dashboard-alerts-reports.md)
- **Status:** Done
- **Depends on:** WI-06-2, WI-05-3
- **Estimate:** M

## Goal

Home dashboard, low-stock page with one-click draft PO creation, and report pages with CSV download.

## Scope

- In: `/` dashboard, nav badge, `/Reports/*` pages.
- Out: charts library (tiles + simple inline SVG sparklines only, if any).

## Tasks

- [x] Dashboard `/`: KPI tiles (links to filtered lists), low-stock top 10, recent movements, POs due this week; refresh via `hx-trigger="every 60s"` on lists.
- [x] Nav "Low stock" badge, server-rendered from the cached counter (see Notes).
- [x] `/Reports/LowStock`: filters, table with checkbox + editable suggested qty per row, "Create draft POs" (CanEdit) → result summary with links to new POs and skipped items.
- [x] `/Reports` index page listing reports.
- [x] Report pages (Valuation, Movements, Usage, PurchaseOrders, DeadStock): filter form (`hx-get`, push URL), paged table, totals row, "Download CSV" link preserving filters.
- [x] Page tests: dashboard renders for Viewer; create draft POs flow; CSV endpoint returns `text/csv` with filename.

## Acceptance criteria

- [x] From an empty dashboard state (no data) every tile/list shows a helpful empty state.
- [x] Creating POs from 5 suggestions across 2 suppliers yields 2 draft POs.


- Dashboard container refreshes itself with `hx-get="/" hx-trigger="every 60s"`; the whole fragment (tiles + lists) is one partial, `_Dashboard.cshtml`.
- Cost and valuation are shown to Viewers: the F06 open question was settled as "everyone who can sign in sees money", matching the rest of the app. Only creating draft POs needs `CanEdit`.

## Notes

- **Badge is server-rendered, not `hx-get` on load.** `_Layout` asks `ILowStockCounter` (60 s cache plus change signal, WI-06-2) directly, so the badge is correct on first paint, costs no extra request per page and needs no endpoint. It sits on its own rail item, "Low stock" (Insights group), not on "Reports".
- **Usage defaults to the last 30 days** when the query has neither `from` nor `to`; the dates are shown in the form so the default is visible.
- **Reorder outcome rides in cookie TempData**, so it is trimmed to the first 8 created orders and skipped items plus a "and N more" count (every order is also a draft in the PO list). A POST with nothing ticked flashes an error and creates nothing.
- **Rows with a draft already open are not pre-ticked** and link to the draft, to avoid double ordering.
- `?handler=Csv` ignores `page`/`pageSize`: the file holds every row for the filter. The "Download CSV" link is built from the current query (`CsvUrl()`), so it follows the filters.
- `RedirectToPage(null, ...)` from a POST handler throws "No page named ''" here; the handler redirects to `/Reports/LowStock` explicitly (found by the flow test).

## As built

- `Pages/Index` + `_Dashboard`; `Pages/Reports/{Index,LowStock,Valuation,Movements,Usage,PurchaseOrders,DeadStock}` each with an `_…Results` partial (filters use `hx-get` + `hx-push-url` into `#results`).
- `Hosting/CsvResult.cs` (`CsvResult` action result, `CsvUrl`, `CsvFileName`); `Pages/Shared/StockDisplay.cs`; `NavItems` gains `Icon`, `LowStockBadge` and `Current`; `_RailItem` renders the badge.
- `site.css` F06 block and a `data-check-all` select-all handler in `site.js`.
- Tests: `Pages/ReportPagesTests` (dashboard empty/filled/htmx, nav badge, every report page for Viewer and anonymous, filters, low-stock flow with 5 parts / 2 suppliers → 2 drafts, Viewer 403, CSV headers/filename/BOM/filters/formula guard); `LayoutAndComponentTests` updated for the new rail.