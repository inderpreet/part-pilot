# WI-00-1 — DbContext, SQLite config, settings table

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Done
- **Depends on:** —
- **Estimate:** M

## Goal

Solution and data layer exist: `AppDbContext` on SQLite with safe pragmas, timestamp handling, migrations that
apply at startup, and an `AppSetting` table with defaults.

## Scope

- In: solution/projects, EF Core setup, interceptors, `AppSetting` entity + seed, initial migration, test DB fixture.
- Out: Identity tables (WI-01-1), domain entities.

## Tasks

- [x] Create `InventoryTracker.sln`, `src/InventoryTracker` (web, net10.0) and `tests/InventoryTracker.Tests` (xUnit).
- [x] `Directory.Build.props`: `Nullable=enable`, `TreatWarningsAsErrors=true`, `ImplicitUsings=enable`.
- [x] Add `Microsoft.EntityFrameworkCore.Sqlite`, `Microsoft.EntityFrameworkCore.Design`; add `dotnet-ef` local tool manifest.
- [x] `Data/AppDbContext.cs` applying configurations from assembly.
- [x] `Data/Interceptors/SqlitePragmaInterceptor` (DbConnectionInterceptor): WAL, foreign_keys, busy_timeout, synchronous.
- [x] `IHasTimestamps` + `TimestampInterceptor` setting `CreatedAt`/`UpdatedAt` UTC.
- [x] `IHasRowVersion` (long) incremented in `SaveChanges` and configured as concurrency token.
- [x] UTC `DateTime` value converter applied by convention (reads come back as `DateTimeKind.Utc`).
- [x] `AppSetting(Key PK, Value)` entity + `HasData` defaults per F00 FR-6.
- [x] Migration `Initial`.
- [x] `DatabaseInitializer`: if pending migrations and DB file exists, copy to `inventory.db.bak-<utc timestamp>`, then `MigrateAsync()`.
- [x] Test fixture `SqliteTestDb` (open in-memory connection, migrate, provide context).

## Acceptance criteria

- [x] Fresh run creates `inventory.db` with `AppSettings` populated; `PRAGMA journal_mode` returns `wal`.
- [x] Adding a dummy migration triggers a `.bak-` copy before migrating.
- [x] Test proves FK enforcement is on and timestamps are set/updated.

## Notes

See architecture §5, §7. Money convention (`long` cents) applies to all later entities.

### Implementation notes

- `RowVersionInterceptor` is separate from `TimestampInterceptor`; both are registered via `AddInventoryInterceptors()`.
- `AppSetting.Key` uses `COLLATE NOCASE`; table is `AppSettings`. Defaults live in `SettingKeys.Defaults`.
- `DatabaseInitializer` runs `PRAGMA wal_checkpoint(TRUNCATE)` before the `.bak-` file copy so the copy is complete under WAL.
- `DesignTimeDbContextFactory` keeps `dotnet ef` from executing startup code.
- Minimal `Program.cs` wiring (DI + migrate at startup) was added so a fresh run creates the DB; WI-00-2 owns the rest of the host.
