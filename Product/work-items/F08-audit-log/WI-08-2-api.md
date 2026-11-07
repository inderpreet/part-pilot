# WI-08-2 — SaveChanges interceptor, AuditService

- **Feature:** [F08 — Audit log](../../Features/F08-audit-log.md)
- **Status:** Planned
- **Depends on:** WI-08-1, WI-01-2
- **Estimate:** M

## Goal

Changes to audited entities and explicit security/business events are recorded automatically in the same transaction.

## Scope

- In: `AuditInterceptor`, `AuditService`, event wiring from F01/F05/F07, queries, tests.
- Out: UI (WI-08-3).

## Tasks

- [ ] `AuditInterceptor : SaveChangesInterceptor`: for `[Audited]` entries in Added/Modified/Deleted, build diff of changed properties
      (skip `[AuditIgnore]`, timestamps, RowVersion); add `AuditEntry` rows before save so they commit atomically. Resolve generated IDs after insert (`SavedChanges` follow-up or temporary-key handling).
- [ ] Mark entities `[Audited]`: Product, Category, Supplier, ProductSupplier, PurchaseOrder, PurchaseOrderLine, ApplicationUser, AppSetting.
- [ ] `ApplicationUser`: ignore PasswordHash, SecurityStamp, ConcurrencyStamp; record role changes as `RoleChanged` event.
- [ ] `AuditService.RecordAsync(action, entityType?, entityId?, details?)` for explicit events; subscribe to F01/F05/F07 hooks
      (SignIn, SignInFailed, LockedOut, SignOut, PasswordChanged, PasswordReset, PoSubmitted, PoCancelled, PoReceived, ImportCommitted, BackupDownloaded).
- [ ] Suppress per-row auditing during import commit; write one `ImportCommitted` summary.
- [ ] Queries: `SearchAsync(filter)` and `GetForEntityAsync(type, id)`; CSV export via `CsvExport`.
- [ ] Tests: update Product cost → one entry with old/new; password hash never appears; failed save writes no audit; sign-in failure recorded.

## Acceptance criteria

- [ ] Every audited entity type has a test proving create/update capture.

## Notes

`ICurrentUser` + `IHttpContextAccessor` supply user and IP; background/system actions use UserName `system`.
