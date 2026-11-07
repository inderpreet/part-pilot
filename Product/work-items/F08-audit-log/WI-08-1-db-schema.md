# WI-08-1 — AuditEntry schema

- **Feature:** [F08 — Audit log](../../Features/F08-audit-log.md)
- **Status:** Planned
- **Depends on:** WI-01-1
- **Estimate:** S

## Goal

Append-only audit table with indexes for entity history and admin filtering.

## Scope

- In: entity, configuration, migration, `[Audited]` / `[AuditIgnore]` attributes.
- Out: capture logic (WI-08-2).

## Tasks

- [ ] `AuditEntry`: Id, OccurredAt, UserId (nullable, no FK so system/anonymous events allowed), UserName (100), Action (50),
      EntityType (100), EntityId (50), ChangesJson (TEXT), IpAddress (45).
- [ ] Indexes: `(EntityType, EntityId)`, `OccurredAt`, `UserId`, `Action`.
- [ ] `[Audited]` attribute on audited entities; `[AuditIgnore]` on sensitive properties.
- [ ] Optional SQLite triggers `BEFORE UPDATE/DELETE ON AuditEntries` raising abort (append-only at DB level).
- [ ] Migration `AddAuditLog`.

## Acceptance criteria

- [ ] Migration applies; UPDATE on an audit row fails if triggers adopted.

## Notes

—
