# F00 — Platform foundation

- **Status:** Done
- **Depends on:** —
- **Roles:** n/a (infrastructure); Settings page is Admin-only (built in F01)

## Summary

Create the solution skeleton that every other feature builds on: ASP.NET Core host, EF Core + SQLite with safe
defaults, base layout with HTMX conventions, application settings storage, and a publish script that produces a
single self-contained executable per platform.

## Functional requirements

1. FR-1 `dotnet run` starts the app on `http://localhost:5080` and creates `inventory.db` if missing.
2. FR-2 Pending EF migrations apply automatically at startup; DB file is backed up first when migrations are pending.
3. FR-3 `--db <path>`, `--urls <url>`, `--bind <localhost|all|ip>`, `--port <n>`, `--open` CLI options work, and the same settings can come from a `.env` file / environment variables (`INVENTORY_BIND`, `INVENTORY_PORT`, `INVENTORY_DB`); see architecture §9.
4. FR-4 `/health` returns 200 with app version and DB status (anonymous).
5. FR-5 Shared layout with top nav, flash/toast area, modal container, and antiforgery header for HTMX.
6. FR-6 `AppSetting` key/value store with typed accessor `ISettingsService` and defaults:
   `Company.Name = "My Company"`, `Currency.Code = "USD"`, `Currency.Symbol = "$"`, `Stock.AllowNegative = false`,
   `PurchaseOrder.NumberPrefix = "PO-"`.
7. FR-7 `scripts/publish.ps1` produces one executable per RID with all static assets embedded.

## Business rules

- All timestamps stored UTC.
- App must start with no files other than the executable (DB created on demand).

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/health` | Liveness + DB check | Anonymous |
| `/Error` | Error page | Anonymous |

## Out of scope

- Authentication (F01), any domain entities.

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-00-1](../work-items/F00-platform-foundation/WI-00-1-db-schema.md) | DbContext, SQLite config, settings table | Done |
| [WI-00-2](../work-items/F00-platform-foundation/WI-00-2-api.md) | Host, DI, config, errors, embedded assets | Done |
| [WI-00-3](../work-items/F00-platform-foundation/WI-00-3-frontend.md) | Layout, HTMX conventions, shared partials | Done |
| [WI-00-4](../work-items/F00-platform-foundation/WI-00-4-packaging.md) | Single-file publish script | Done |

## Open questions

- ~~Default port 5080 OK? Bind to localhost only by default, or all interfaces?~~ **Resolved:** port 5080; binds to localhost by default; INVENTORY_BIND=all (in .env, env var, or --bind all) opens it to the LAN.
- **Pending manual verification** (WI-00-4): run the published exe on a machine with no .NET installed; run `scripts/smoke.sh` and the linux-x64 binary on a Linux host. Both scripts and all four binaries build; only win-x64 has been executed.
