# WI-00-2 — Host, DI, config, errors, embedded assets

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Done
- **Depends on:** WI-00-1
- **Estimate:** M

## Goal

`Program.cs` wires the app: configuration/CLI, DB, services, Razor Pages, embedded static files, error handling,
logging and a health endpoint. Shared service-layer primitives exist for later features.

## Scope

- In: host, CLI args, `ISettingsService`, `Result` types, `/health`, error handling, logging, embedded `wwwroot`.
- Out: authentication (WI-01-2), UI layout (WI-00-3).

## Tasks

- [x] CLI parsing: `--db`, `--urls`, `--open` mapped into configuration; default DB path `AppContext.BaseDirectory/inventory.db`.
- [x] Default URL `http://localhost:5080`, switchable to all interfaces via `INVENTORY_BIND` / `--bind` (`.env`, env var or CLI). Added `--port`, `INVENTORY_PORT`, `INVENTORY_DB`.
- [x] Register `AppDbContext` (scoped) with interceptors; run `DatabaseInitializer` at startup.
- [x] `Services/Result.cs`: `Result`, `Result<T>`, `Error(code, message, field?)`.
- [x] `Services/SettingsService.cs` (`ISettingsService`): typed getters/setters with defaults, in-memory cache invalidated on write.
- [x] `Services/IClock` (`SystemClock`) for testable time.
- [x] Embedded static files: `GenerateEmbeddedFilesManifest=true`, `<EmbeddedResource Include="wwwroot/**" />`,
      `UseStaticFiles` with `ManifestEmbeddedFileProvider` (fall back to physical files in Development for hot edit).
- [x] `UseExceptionHandler("/Error")`, `UseStatusCodePagesWithReExecute("/Error/{0}")`; HTMX-aware error partial.
- [x] `/health` minimal endpoint: `{ status, version, db: "ok" }` (runs `SELECT 1`).
- [x] Logging: console + simple rolling file in `<db dir>/logs/`.
- [x] `--open` launches default browser after server start (Process.Start with UseShellExecute).
- [x] `UseWindowsService()` / `UseSystemd()` enabled (no-ops when not hosted as service).
- [x] `HtmxExtensions`: `IsHtmx(this HttpRequest)`, `HxTrigger`, `HxRedirect` helpers.
- [x] `WebApplicationFactory` test fixture using a temp DB.

## Acceptance criteria

- [x] `dotnet run -- --db C:\temp\x.db --urls http://localhost:6000` uses those values.
- [x] `/health` returns 200 JSON; returns 503 if DB unreachable.
- [x] Static file `/js/site.js` served with no physical `wwwroot` (htmx itself is vendored in WI-00-3 under `/lib/`; the embedded-provider mechanism is proven here with `site.js`).
- [x] Unhandled exception returns error page (full) or error partial (HTMX request).

## Notes

See architecture §9, §11.

### Implementation notes

- **Config precedence:** CLI > env vars > `.env` > beside-exe `appsettings.json` > in-code defaults. `DotEnv` exports `.env` into process env vars *before* the host builder runs, never overriding real env vars. `HostSettings.Resolve` turns the keys into URLs / DB path / log dir (unit-tested).
- `--open` is stripped from args before configuration parsing (a bare flag would swallow the next option in the .NET command-line provider).
- `launchSettings.json` no longer sets `applicationUrl`; otherwise `ASPNETCORE_URLS` from the profile would outrank `INVENTORY_BIND` during `dotnet run`.
- `HostSettings`, DB connection string and the file-log provider are resolved lazily from `IConfiguration` so `WebApplicationFactory` overrides are honoured.
- `/health` uses ASP.NET health checks (`DatabaseHealthCheck` = `SELECT 1`); 503 comes from the framework when any check is unhealthy.
- `IClock`/`SystemClock` exist per spec; interceptors keep using the built-in `TimeProvider` (prefer `TimeProvider` for new code unless a spec says otherwise).
- Test-only `/__fault` endpoint exists only in the `Testing` environment to exercise the exception handler.
- `Error` page is unstyled (`Layout = null`) until WI-00-3 provides the layout. htmx does not swap 4xx/5xx responses by default; WI-00-3's `site.js` must opt in (`htmx:beforeSwap`) for the `#flash` retarget to display.
- Packages added: see architecture decisions D8–D10.
