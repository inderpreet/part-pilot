# F04 — Suppliers

- **Status:** In Progress
- **Depends on:** F02
- **Roles:** Admin/Staff create and edit; Viewer reads

## Summary

Keep a directory of suppliers and record which suppliers provide which products, at what cost, under what
supplier SKU and lead time. Feeds purchase orders (F05) and reorder suggestions (F06).

## User stories

- As Staff, I want to store supplier contact details so that ordering is quick.
- As Staff, I want to link a product to one or more suppliers with their SKU and cost, and mark one as preferred.
- As any user, I want to see all products a supplier provides.

## Functional requirements

1. FR-1 Supplier fields: Name (required, unique), ContactName, Email, Phone, Address (multi-line), Website, Notes, IsActive.
2. FR-2 Supplier list: search by name/contact/email, active filter, paging.
3. FR-3 Supplier detail: contact info, linked products table, recent POs (after F05).
4. FR-4 Product–supplier link fields: SupplierSku, UnitCost, LeadTimeDays, MinOrderQuantity, IsPreferred.
   Manageable from both the supplier detail and the product detail page (inline HTMX rows).
5. FR-5 Archive/restore supplier.

## Business rules

- At most one preferred supplier per product (setting one clears the others).
- A product–supplier pair is unique.
- Archived suppliers cannot be chosen for new POs; existing POs unaffected.
- Removing a link does not affect existing POs.

## Data model

`Supplier`, `ProductSupplier` — see architecture §5.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Suppliers` | List/search | CanView |
| `/Suppliers/{id}` | Detail + linked products | CanView |
| `/Suppliers/Edit/{id?}` | Create/edit | CanEdit |
| `/Suppliers/{id}?handler=Link` / `Unlink` (POST) | Manage product links | CanEdit |
| `/Products/{id}?handler=Suppliers` | Product's suppliers partial | CanView |

## Out of scope

- Supplier price breaks, multiple currencies, supplier portals, supplier performance scoring.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-04-1](../work-items/F04-suppliers/WI-04-1-db-schema.md) | Supplier, ProductSupplier schema | Done |
| [WI-04-2](../work-items/F04-suppliers/WI-04-2-api.md) | SupplierService | Done |
| [WI-04-3](../work-items/F04-suppliers/WI-04-3-frontend.md) | Supplier list/detail/edit, product links | Planned |

## Open questions

- Multiple contacts per supplier needed, or one contact enough?
