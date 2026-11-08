# F06 — Dashboard, alerts & reports

- **Status:** Done
- **Depends on:** F03, F05
- **Roles:** All users view; Admin/Staff create draft POs from suggestions

## Summary

Give users an at-a-glance home page, surface low-stock items in-app, suggest what to reorder, and provide a
handful of standard reports with CSV export.

## User stories

- As any user, I want a dashboard showing key numbers and recent activity when I sign in.
- As Staff, I want to see which products are low on stock, accounting for what is already on order.
- As Staff, I want to turn reorder suggestions into draft POs grouped by preferred supplier.
- As a manager (Admin), I want a stock valuation report and a movement report for a date range.

## Functional requirements

1. FR-1 Dashboard tiles: active SKUs, total stock value, low-stock count, out-of-stock count, open POs (count + value), POs overdue (ExpectedDate passed).
2. FR-2 Dashboard lists: top 10 low-stock items, last 10 stock movements, open POs due this week.
3. FR-3 Nav badge with low-stock count on every page (cached ~60 s; rendered server-side from the cached counter on the `Low stock` rail item, see WI-06-3).
4. FR-4 Low-stock page: products where on hand + on order <= reorder point; columns: SKU, name, on hand, on order,
   reorder point, suggested qty, preferred supplier; filter by category/supplier.
5. FR-5 Suggested quantity = max(ReorderQuantity, ReorderPoint − (OnHand + OnOrder)), rounded up to supplier MinOrderQuantity (shared helper `MinOrderRounding.RoundUp(quantity, minOrderQuantity)`, already in `Services/MinOrderRounding.cs` from F10; reuse it, do not copy it).
6. FR-6 "Create draft POs": select suggestions → one Draft PO per preferred supplier; items without a preferred supplier are listed as skipped.
7. FR-7 Reports (each with filters, on-screen table, CSV download):
   - Stock on hand & valuation (as of now; by category)
   - Stock movements (date range, type, product, category, user)
   - Usage / issues by product (date range) — top consumers
   - PO summary (by supplier, status, date range)
   - Dead stock (no movement in N days, default 90)

## Business rules

- Products with ReorderPoint = 0 or inactive are never low stock.
- Values use Product.UnitCost x on hand (no historical costing in v1).
- Report CSV respects the same filters as the screen.

## Data model

No new tables. Indexes + read-model queries — see WI-06-1.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/` | Dashboard | CanView |
| `/Reports/LowStock` | Low stock + suggestions | CanView (create POs: CanEdit) |
| `/Reports/Valuation`, `/Reports/Movements`, `/Reports/Usage`, `/Reports/PurchaseOrders`, `/Reports/DeadStock` | Reports | CanView |
| `...?handler=Csv` | CSV download for each report | CanView |

## Out of scope

- Email/push notifications, charts beyond simple tiles/sparklines, scheduled reports, forecasting.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-06-1](../work-items/F06-dashboard-alerts-reports/WI-06-1-db-schema.md) | Query indexes, reporting queries | Done |
| [WI-06-2](../work-items/F06-dashboard-alerts-reports/WI-06-2-api.md) | ReportService, reorder suggestions | Done |
| [WI-06-3](../work-items/F06-dashboard-alerts-reports/WI-06-3-frontend.md) | Dashboard, low-stock, reports pages | Done |

## Open questions

- Any additional reports needed in v1?
- Usage counts Issue movements only; a reversed issue is not netted off, so usage can be slightly overstated. Net it (issues minus their reversals) if that matters in practice.
- ~~Should Viewers see cost/valuation figures?~~ Resolved: yes, every signed-in user sees cost and valuation figures (decided with F10 FR-11).
