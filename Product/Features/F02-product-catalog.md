# F02 — Product catalog

- **Status:** Done
- **Depends on:** F01
- **Roles:** Admin/Staff create and edit; Viewer reads

## Summary

Maintain the list of items the business stocks: identity (SKU, name, barcode), classification (category),
where it lives (bin location), how it is counted (unit), its cost, and reorder thresholds.

## User stories

- As Staff, I want to add a product with SKU, name, unit, bin, cost and reorder point so that it can be tracked.
- As any user, I want to search products by SKU, name or barcode (including from a barcode scanner) and filter by category.
- As Staff, I want to archive a product that is no longer stocked without losing its history.
- As Staff, I want to organise products into categories.

## Functional requirements

1. FR-1 Product fields: SKU (required, unique, max 40), Name (required, max 200), Description, Category (optional),
   Unit (e.g. "each", "box"; default "each"), Barcode (optional, unique), BinLocation (free text, max 50),
   ReorderPoint (>= 0), ReorderQuantity (>= 0), UnitCost (>= 0, currency), IsActive.
2. FR-2 Product list: live search (SKU/name/barcode), category filter, active/archived/all filter,
   sortable columns, paging (25/page), shows on hand and low-stock badge.
3. FR-3 Product detail page: fields, current on hand; later extended with stock history (F03), suppliers (F04), open POs (F05).
4. FR-4 Create, edit, archive, restore product. Exact barcode/SKU match in search jumps straight to detail.
5. FR-5 Category CRUD: Name (unique), Description, IsActive. Archiving a category leaves its products assigned.
6. FR-6 QuantityOnHand is displayed but **not editable** here (changes only via F03).

## Business rules

- SKU and Barcode unique case-insensitively across active and archived products.
- SKU editable while the product has no stock transactions; read-only afterwards (keeps history/exports consistent).
- Archived products hidden from pickers (stock movements, PO lines) but visible in reports/history.
- Concurrent edits detected via RowVersion: user sees "modified by someone else, reload".

## Data model

`Category`, `Product` — see architecture §5.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Products` | List/search | CanView |
| `/Products/{id}` | Detail | CanView |
| `/Products/Edit/{id?}` | Create/edit | CanEdit |
| `/Products/{id}?handler=Archive` (POST) | Archive/restore | CanEdit |
| `/Products/Lookup?q=` | HTMX picker partial reused by other features | CanView |
| `/Categories` | Category list; inline create/edit/archive via handlers `New`, `Edit`, `Row`, `Save`, `SetActive` (the data-changing ones re-check `CanEdit`) | CanView (handlers: CanEdit) |

## Out of scope

- Product variants/options, images, kits/bundles, multiple units of measure with conversion, sale prices.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-02-1](../work-items/F02-product-catalog/WI-02-1-db-schema.md) | Product, Category schema | Done |
| [WI-02-2](../work-items/F02-product-catalog/WI-02-2-api.md) | ProductService, CategoryService | Done |
| [WI-02-3](../work-items/F02-product-catalog/WI-02-3-frontend.md) | Product list/detail/edit, categories | Done |

## Open questions

- Need product images? (Would require file storage beside the DB.)
- Need nested categories? v1 is flat.
- Need a sale price field even without sales features?
