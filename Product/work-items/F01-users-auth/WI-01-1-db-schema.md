# WI-01-1 — Identity schema, role seed

- **Feature:** [F01 — Users & authentication](../../Features/F01-users-auth.md)
- **Status:** Done
- **Depends on:** WI-00-1
- **Estimate:** S

## Goal

Identity tables exist in the SQLite DB with an extended user entity and the three roles seeded.

## Scope

- In: `ApplicationUser`, Identity EF stores, role seed, migration.
- Out: auth middleware/policies (WI-01-2).

## Tasks

- [x] Add `Microsoft.AspNetCore.Identity.EntityFrameworkCore`.
- [x] `ApplicationUser : IdentityUser` with `DisplayName` (required, max 100), `IsActive` (default true), `LastSignInAt` (UTC, nullable).
- [x] `AppDbContext : IdentityDbContext<ApplicationUser, IdentityRole, string>`.
- [x] `Roles` static class: `Admin`, `Staff`, `Viewer` constants.
- [x] Seed roles via `HasData` with fixed IDs and concurrency stamps.
- [x] `NormalizedUserName` unique index (Identity default) — confirm case-insensitive behaviour.
- [x] Migration `AddIdentity`.

## Acceptance criteria

- [x] Fresh DB has `AspNetUsers`, `AspNetRoles` (3 rows), `AspNetUserRoles`, etc.
- [x] Migration applies cleanly on top of the WI-00-1 schema.

## Notes

User IDs are strings (GUID). Other tables reference users via `UserId` string FK.
