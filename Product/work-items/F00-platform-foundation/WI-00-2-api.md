# WI-00-2 — Host, DI, config, errors, embedded assets

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Planned
- **Depends on:** WI-00-1
- **Estimate:** M

## Goal

`Program.cs` wires the app: configuration/CLI, DB, services, Razor Pages, embedded static files, error handling,
logging and a health endpoint. Shared service-layer primitives exist for later features.

## Scope

- In: host, CLI args, `ISettingsService`, `Result` types, `/health`, error handling, logging, embedded `wwwroot`.
- Out: authentication (WI-01-2), UI layout (WI-00-3).

## Tasks

- [ ] CLI parsing: `--db`, `--urls`, `--open` mapped into configuration; default DB path `AppContext.BaseDirectory/inventory.db`.
- [ ] Default URL `http://localhost:5080`.
- [ ] Register `AppDbContext` (scoped) with interceptors; run `DatabaseInitializer` at startup.
- [ ] `Services/Result.cs`: `Result`, `Result<T>`, `Error(code, message, field?)`.
- [ ] `Services/SettingsService.cs` (`ISettingsService`): typed getters/setters with defaults, in-memory cache invalidated on write.
- [ ] `Services/IClock` (`SystemClock`) for testable time.
- [ ] Embedded static files: `GenerateEmbeddedFilesManifest=true`, `<EmbeddedResource Include="wwwroot/**" />`,
      `UseStaticFiles` with `ManifestEmbeddedFileProvider` (fall back to physical files in Development for hot edit).
- [ ] `UseExceptionHandler("/Error")`, `UseStatusCodePagesWithReExecute("/Error/{0}")`; HTMX-aware error partial.
- [ ] `/health` minimal endpoint: `{ status, version, db: "ok" }` (runs `SELECT 1`).
- [ ] Logging: console + simple rolling file in `<db dir>/logs/`.
- [ ] `--open` launches default browser after server start (Process.Start with UseShellExecute).
- [ ] `UseWindowsService()` / `UseSystemd()` enabled (no-ops when not hosted as service).
- [ ] `HtmxExtensions`: `IsHtmx(this HttpRequest)`, `HxTrigger`, `HxRedirect` helpers.
- [ ] `WebApplicationFactory` test fixture using a temp DB.

## Acceptance criteria

- [ ] `dotnet run -- --db C:\temp\x.db --urls http://localhost:6000` uses those values.
- [ ] `/health` returns 200 JSON; returns 503 if DB unreachable.
- [ ] Static file `/js/htmx.min.js` served with the physical `wwwroot` folder deleted from publish output.
- [ ] Unhandled exception returns error page (full) or error partial (HTMX request).

## Notes

See architecture §9, §11.
