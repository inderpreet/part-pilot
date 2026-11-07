# WI-06-3 — Dashboard, low-stock, reports pages

- **Feature:** [F06 — Dashboard, alerts & reports](../../Features/F06-dashboard-alerts-reports.md)
- **Status:** Planned
- **Depends on:** WI-06-2, WI-05-3
- **Estimate:** M

## Goal

Home dashboard, low-stock page with one-click draft PO creation, and report pages with CSV download.

## Scope

- In: `/` dashboard, nav badge, `/Reports/*` pages.
- Out: charts library (tiles + simple inline SVG sparklines only, if any).

## Tasks

- [ ] Dashboard `/`: KPI tiles (links to filtered lists), low-stock top 10, recent movements, POs due this week; refresh via `hx-trigger="every 60s"` on lists.
- [ ] Nav "Low stock" badge partial loaded with `hx-get` on page load.
- [ ] `/Reports/LowStock`: filters, table with checkbox + editable suggested qty per row, "Create draft POs" (CanEdit) → result summary with links to new POs and skipped items.
- [ ] `/Reports` index page listing reports.
- [ ] Report pages (Valuation, Movements, Usage, PurchaseOrders, DeadStock): filter form (`hx-get`, push URL), paged table, totals row, "Download CSV" link preserving filters.
- [ ] Page tests: dashboard renders for Viewer; create draft POs flow; CSV endpoint returns `text/csv` with filename.

## Acceptance criteria

- [ ] From an empty dashboard state (no data) every tile/list shows a helpful empty state.
- [ ] Creating POs from 5 suggestions across 2 suppliers yields 2 draft POs.

## Notes

If cost visibility for Viewers is restricted (open question in F06), hide money columns via policy check.
