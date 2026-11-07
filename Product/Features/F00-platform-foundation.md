# F00 — Platform foundation

- **Status:** Planned
- **Depends on:** —
- **Roles:** n/a (infrastructure); Settings page is Admin-only (built in F01)

## Summary

Create the solution skeleton that every other feature builds on: ASP.NET Core host, EF Core + SQLite with safe
defaults, base layout with HTMX conventions, application settings storage, and a publish script that produces a
single self-contained executable per platform.

## Functional requirements

1. FR-1 `dotnet run` starts the app on `http://localhost:5080` and creates `inventory.db` if missing.
2. FR-2 Pending EF migrations apply automatically at startup; DB file is backed up first when migrations are pending.
3. FR-3 `--db <path>`, `--urls <url>`, `--open` CLI options work (see architecture §9).
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
| [WI-00-1](../work-items/F00-platform-foundation/WI-00-1-db-schema.md) | DbContext, SQLite config, settings table | Planned |
| [WI-00-2](../work-items/F00-platform-foundation/WI-00-2-api.md) | Host, DI, config, errors, embedded assets | Planned |
| [WI-00-3](../work-items/F00-platform-foundation/WI-00-3-frontend.md) | Layout, HTMX conventions, shared partials | Planned |
| [WI-00-4](../work-items/F00-platform-foundation/WI-00-4-packaging.md) | Single-file publish script | Planned |

## Open questions

- Default port 5080 OK? Bind to localhost only by default, or all interfaces so tablets on the LAN can connect?
