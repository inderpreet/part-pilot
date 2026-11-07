# WI-00-1 — DbContext, SQLite config, settings table

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Planned
- **Depends on:** —
- **Estimate:** M

## Goal

Solution and data layer exist: `AppDbContext` on SQLite with safe pragmas, timestamp handling, migrations that
apply at startup, and an `AppSetting` table with defaults.

## Scope

- In: solution/projects, EF Core setup, interceptors, `AppSetting` entity + seed, initial migration, test DB fixture.
- Out: Identity tables (WI-01-1), domain entities.

## Tasks

- [ ] Create `InventoryTracker.sln`, `src/InventoryTracker` (web, net10.0) and `tests/InventoryTracker.Tests` (xUnit).
- [ ] `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`.
- [ ] Add `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`; add `dotnet-ef` local tool manifest.
- [ ] `Data/AppDbContext.cs` applying configurations from assembly.
- [ ] `Data/Interceptors/SqlitePragmaInterceptor` (DbConnectionInterceptor): WAL, foreign_keys, busy_timeout, synchronous.
- [ ] `IHasTimestamps` + `TimestampInterceptor` setting `CreatedAt`/`UpdatedAt` UTC.
- [ ] `IHasRowVersion` (long) incremented in `SaveChanges` and configured as concurrency token.
- [ ] UTC `DateTime` value converter applied by convention (reads come back as `DateTimeKind.Utc`).
- [ ] `AppSetting(Key PK, Value)` entity + `HasData` defaults per F00 FR-6.
- [ ] Migration `Initial`.
- [ ] `DatabaseInitializer`: if pending migrations and DB file exists, copy to `inventory.db.bak-<utc timestamp>`, then `MigrateAsync()`.
- [ ] Test fixture `SqliteTestDb` (open in-memory connection, migrate, provide context).

## Acceptance criteria

- [ ] Fresh run creates `inventory.db` with `AppSettings` populated; `PRAGMA journal_mode` returns `wal`.
- [ ] Adding a dummy migration triggers a `.bak-` copy before migrating.
- [ ] Test proves FK enforcement is on and timestamps are set/updated.

## Notes

See architecture §5, §7. Money convention (`long` cents) applies to all later entities.
