# Inventory Tracker — Product

Self-hosted inventory tracking for a small business with a **single stock location**. Distributed as one
executable: run it, open the browser, sign in. Data lives in a single SQLite file next to the executable.

- Technical design: [architecture.md](architecture.md)
- Feature specs: [Features/](Features/)
- Work items: [work-items/](work-items/)
- Templates: [templates/](templates/)

## Users and roles

| Role | Can do |
|---|---|
| **Admin** | Everything, plus user management, settings, audit log, backups, imports |
| **Staff** | Create/edit products, suppliers, POs; record stock movements; receive POs; export |
| **Viewer** | Read-only access to catalog, stock, POs, dashboard, reports |

## Features

| ID | Feature | Summary | Depends on | Status |
|---|---|---|---|---|
| [F00](Features/F00-platform-foundation.md) | Platform foundation | Project skeleton, SQLite + EF Core, layout, settings, single-file publish | — | Done |
| [F01](Features/F01-users-auth.md) | Users & authentication | Login, roles, first-run admin setup, user management | F00 | Done |
| [F02](Features/F02-product-catalog.md) | Product catalog | Products (SKU, unit, bin, reorder point, cost) and categories | F01 | Done |
| [F03](Features/F03-stock-tracking.md) | Stock tracking | Stock ledger: receive, issue, adjust, count; on-hand quantity | F02 | Done |
| [F04](Features/F04-suppliers.md) | Suppliers | Supplier directory and product–supplier pricing | F02 | Done |
| [F05](Features/F05-purchase-orders.md) | Purchase orders & receiving | PO lifecycle, receiving into stock | F03, F04 | Done |
| [F06](Features/F06-dashboard-alerts-reports.md) | Dashboard, alerts & reports | KPIs, low-stock alerts, reorder suggestions, reports | F03, F05 | In Progress |
| [F07](Features/F07-data-import-export.md) | Data import/export & backup | CSV import/export, opening stock, DB backup download | F03, F04 | Planned |
| [F08](Features/F08-audit-log.md) | Audit log | Who changed what and when; sign-in events | F01 | Planned |
| [F09](Features/F09-ui-overhaul.md) | UI overhaul | Left rail shell, list cards, actions, forms and detail pages on the screens that exist | F05 | Done |
| [F10](Features/F10-project-boms.md) | Projects & BOMs | Numbered projects with bills of materials from catalog or new parts, status check against stock, orders and draft POs, order shortages, build (issue parts) | F02, F03, F04, F05, F09 | Done |

Suggested build order: F00 → F01 → F02 → F03 → F04 → F05 → F09 → F10 → F08 → F06 → F07 (F10 is independent of F06–F08 and can move).
F09 is a presentation overhaul of F00–F05 (shipped; the shared patterns are shown at `/Dev/Components`). F06, F07 and F08 should use its shared patterns (architecture D17) when they build screens.

## Work items

Each feature is split into three work items, done in order: **1 DB schema → 2 API (services + endpoints) → 3 Frontend**.
F00 has an extra packaging item. F09 has no schema and no service layer; it is split into incremental UI work items (foundation, shell, header, lists, actions, forms, detail, finish) instead.

| ID | Title | Status |
|---|---|---|
| [WI-00-1](work-items/F00-platform-foundation/WI-00-1-db-schema.md) | Foundation — DbContext, SQLite config, settings table | Done |
| [WI-00-2](work-items/F00-platform-foundation/WI-00-2-api.md) | Foundation — host, DI, config, errors, embedded assets | Done |
| [WI-00-3](work-items/F00-platform-foundation/WI-00-3-frontend.md) | Foundation — layout, HTMX conventions, shared partials | Done |
| [WI-00-4](work-items/F00-platform-foundation/WI-00-4-packaging.md) | Foundation — single-file publish script | Done |
| [WI-00-5](work-items/F00-platform-foundation/WI-00-5-branding.md) | Foundation — branding from configuration | Done |
| [WI-00-6](work-items/F00-platform-foundation/WI-00-6-icon.md) | Foundation — app icon from the logo | Done |
| [WI-01-1](work-items/F01-users-auth/WI-01-1-db-schema.md) | Users — Identity schema, role seed | Done |
| [WI-01-2](work-items/F01-users-auth/WI-01-2-api.md) | Users — auth config, policies, UserService | Done |
| [WI-01-3](work-items/F01-users-auth/WI-01-3-frontend.md) | Users — login, setup, user admin, settings pages | Done |
| [WI-02-1](work-items/F02-product-catalog/WI-02-1-db-schema.md) | Catalog — Product, Category schema | Done |
| [WI-02-2](work-items/F02-product-catalog/WI-02-2-api.md) | Catalog — ProductService, CategoryService | Done |
| [WI-02-3](work-items/F02-product-catalog/WI-02-3-frontend.md) | Catalog — product list/detail/edit, categories | Done |
| [WI-03-1](work-items/F03-stock-tracking/WI-03-1-db-schema.md) | Stock — StockTransaction ledger | Done |
| [WI-03-2](work-items/F03-stock-tracking/WI-03-2-api.md) | Stock — StockService | Done |
| [WI-03-3](work-items/F03-stock-tracking/WI-03-3-frontend.md) | Stock — movements, history, stock count | Done |
| [WI-04-1](work-items/F04-suppliers/WI-04-1-db-schema.md) | Suppliers — Supplier, ProductSupplier schema | Done |
| [WI-04-2](work-items/F04-suppliers/WI-04-2-api.md) | Suppliers — SupplierService | Done |
| [WI-04-3](work-items/F04-suppliers/WI-04-3-frontend.md) | Suppliers — list/detail/edit, product links | Done |
| [WI-05-1](work-items/F05-purchase-orders/WI-05-1-db-schema.md) | POs — PurchaseOrder, PurchaseOrderLine schema | Done |
| [WI-05-2](work-items/F05-purchase-orders/WI-05-2-api.md) | POs — PurchaseOrderService, receiving | Done |
| [WI-05-3](work-items/F05-purchase-orders/WI-05-3-frontend.md) | POs — list, editor, receive, print view | Done |
| [WI-06-1](work-items/F06-dashboard-alerts-reports/WI-06-1-db-schema.md) | Dashboard — query indexes, reporting queries | Done |
| [WI-06-2](work-items/F06-dashboard-alerts-reports/WI-06-2-api.md) | Dashboard — ReportService, reorder suggestions | Planned |
| [WI-06-3](work-items/F06-dashboard-alerts-reports/WI-06-3-frontend.md) | Dashboard — dashboard, low-stock, reports pages | Planned |
| [WI-07-1](work-items/F07-data-import-export/WI-07-1-db-schema.md) | Import/export — ImportBatch schema, CSV formats | Planned |
| [WI-07-2](work-items/F07-data-import-export/WI-07-2-api.md) | Import/export — Import/Export/Backup services | Planned |
| [WI-07-3](work-items/F07-data-import-export/WI-07-3-frontend.md) | Import/export — upload, preview, export, backup pages | Planned |
| [WI-08-1](work-items/F08-audit-log/WI-08-1-db-schema.md) | Audit — AuditEntry schema | Planned |
| [WI-08-2](work-items/F08-audit-log/WI-08-2-api.md) | Audit — SaveChanges interceptor, AuditService | Planned |
| [WI-08-3](work-items/F08-audit-log/WI-08-3-frontend.md) | Audit — audit log viewer, entity history | Planned |
| [WI-09-0](work-items/F09-ui-overhaul/WI-09-0-reference.md) | UI — reference page (`/Dev/Overhaul`, retired in WI-09-8) | Done |
| [WI-09-1](work-items/F09-ui-overhaul/WI-09-1-foundation.md) | UI — foundation: tokens, type, controls, chips, tables | Done |
| [WI-09-2](work-items/F09-ui-overhaul/WI-09-2-shell.md) | UI — shell: rail, top bar, skip link, drawer | Done |
| [WI-09-3](work-items/F09-ui-overhaul/WI-09-3-page-header.md) | UI — page header | Done |
| [WI-09-4](work-items/F09-ui-overhaul/WI-09-4-lists.md) | UI — list cards | Done |
| [WI-09-5](work-items/F09-ui-overhaul/WI-09-5-actions.md) | UI — button roles, overflow menu, confirm dialog | Done |
| [WI-09-6](work-items/F09-ui-overhaul/WI-09-6-forms.md) | UI — form sections and field errors | Done |
| [WI-09-7](work-items/F09-ui-overhaul/WI-09-7-detail.md) | UI — detail pages | Done |
| [WI-09-8](work-items/F09-ui-overhaul/WI-09-8-finish.md) | UI — narrow screens, print, gallery, clean-up | Done |
| [WI-10-1](work-items/F10-project-boms/WI-10-1-db-schema.md) | Projects — Project, BomLine, ProjectBuild schema | Done |
| [WI-10-2](work-items/F10-project-boms/WI-10-2-api.md) | Projects — ProjectService (BOM, status, build), PO add-lines | Done |
| [WI-10-3](work-items/F10-project-boms/WI-10-3-frontend-projects.md) | Projects — list, project page, BOM editing, new part | Done |
| [WI-10-4](work-items/F10-project-boms/WI-10-4-frontend-order-build.md) | Projects — order shortages, build, builds history | Done |

## Non-functional requirements

- Runs on Windows, Linux, macOS from a single executable; no installer, no runtime prerequisites.
- Handles ~10k products, ~1M stock transactions, ~10 concurrent users on modest hardware.
- Page responses < 300 ms for list/search at that scale.
- Works in current evergreen browsers; usable on a tablet (receiving at the dock).
- All data in one SQLite file; backup = copy/download that file.

## Out of scope (v1)

- Multiple warehouses/locations and transfers between them
- Sales orders, invoicing, customers, POS
- Multi-tenant hosting
- Barcode scanner hardware integration beyond keyboard-wedge input into search fields
- Public JSON API (see architecture open questions)
- Email/SMS notifications (alerts are in-app only)

## Glossary

- **SKU** — unique product code chosen by the business.
- **On hand** — quantity physically in stock (`Product.QuantityOnHand`).
- **On order** — quantity on submitted, not-yet-received PO lines.
- **Reorder point** — when on hand + on order falls to or below this, the product is "low stock".
- **Stock transaction** — immutable ledger row recording a change in on-hand quantity.
