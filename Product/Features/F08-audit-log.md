# F08 — Audit log

- **Status:** Planned
- **Depends on:** F01 (hooks into entities from F02–F07 as they exist)
- **Roles:** Admin views full log; all users see per-entity "history" on detail pages they can view

## Summary

Record who changed what and when for master data, purchase orders, users and settings, plus sign-in events.
Stock movements are already an immutable ledger (F03) and are not duplicated here.

## User stories

- As an Admin, I want to see who changed a product's cost and what the old value was.
- As an Admin, I want to see sign-in attempts, including failures and lockouts.
- As any user, I want to see the change history on a product, supplier or PO page.

## Functional requirements

1. FR-1 Automatically capture Create/Update/Delete on audited entities: Product, Category, Supplier, ProductSupplier,
   PurchaseOrder, PurchaseOrderLine, ApplicationUser (excluding password hash/security stamp), AppSetting.
2. FR-2 Store changed fields only, as JSON `{ "Field": { "old": ..., "new": ... } }`; money shown formatted in UI.
3. FR-3 Explicit events: SignIn, SignInFailed, LockedOut, SignOut, PasswordChanged, PasswordReset, RoleChanged,
   PoSubmitted, PoCancelled, PoReceived, ImportCommitted, BackupDownloaded.
4. FR-4 Admin audit viewer: filter by date range, user, entity type, action, entity id; paging; CSV export.
5. FR-5 "History" tab on Product, Supplier, PO detail pages listing entries for that entity.
6. FR-6 Audit entries are written in the same DB transaction as the change.

## Business rules

- Audit entries are append-only; no UI or service to edit/delete.
- Retention: keep forever in v1 (document size impact).
- Sensitive fields (PasswordHash, SecurityStamp, ConcurrencyStamp) never logged.

## Data model

`AuditEntry` — see architecture §5. Indexes `(EntityType, EntityId)`, `(OccurredAt)`, `(UserId)`.

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Admin/Audit` | Audit viewer | RequireAdmin |
| `/Admin/Audit?handler=Csv` | Export | RequireAdmin |
| `/Audit/Entity/{type}/{id}` | Entity history partial | CanView |

## Out of scope

- Tamper-evidence (hash chaining), external log shipping, retention policies.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-08-1](../work-items/F08-audit-log/WI-08-1-db-schema.md) | AuditEntry schema | Planned |
| [WI-08-2](../work-items/F08-audit-log/WI-08-2-api.md) | SaveChanges interceptor, AuditService | Planned |
| [WI-08-3](../work-items/F08-audit-log/WI-08-3-frontend.md) | Audit log viewer, entity history | Planned |

## Open questions

- Retention limit needed (e.g. purge after 2 years)?
