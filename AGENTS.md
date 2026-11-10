# AGENTS.md — Inventory Tracker

Canonical instructions for all AI coding agents (Claude Code, Copilot, Codex, Cursor, etc.).
Tool-specific files (`CLAUDE.md`, `.github/copilot-instructions.md`) defer to this file.

## Project

Inventory tracking app for a **single business, single location**. Ships as **one self-contained executable**
that serves a server-rendered web UI and stores data in a local **SQLite** file.

- Product docs: `Product/README.md` (feature index) and `Product/architecture.md` (technical design)
- Feature specs: `Product/Features/Fxx-*.md`
- Work items: `Product/work-items/Fxx-*/WI-xx-n-*.md`

Read `Product/architecture.md` before writing code. Read the relevant feature spec and work item before implementing.

## Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), C# |
| Web | ASP.NET Core Razor Pages + HTMX (vendored, no Node build) |
| CSS | Pico.css (vendored) + `site.css` |
| Data | EF Core + `Microsoft.EntityFrameworkCore.Sqlite` |
| Auth | ASP.NET Core Identity, cookie auth, roles |
| CSV | CsvHelper |
| Tests | xUnit, `WebApplicationFactory`, SQLite in-memory |
| Packaging | `dotnet publish` self-contained single file, static assets embedded |

## Commands

Dev helper (PowerShell): `./scripts/dev.ps1 [run|watch|test|build|migrate|reset-db|help]` — e.g. `./scripts/dev.ps1 run -Lan -Open`,
`./scripts/dev.ps1 test -Filter "FullyQualifiedName~Settings"`, `./scripts/dev.ps1 migrate -Name AddProducts`.

```bash
dotnet build
dotnet test
dotnet run --project src/InventoryTracker
dotnet ef migrations add <Name> --project src/InventoryTracker --output-dir Data/Migrations
./scripts/publish.ps1 -Runtime win-x64     # or linux-x64, osx-arm64
```

## Workflow

1. Every change maps to a **work item** (`WI-xx-n`). If none exists, create one from `Product/templates/work-item-template.md`.
2. Work items per feature follow the order: `1-db-schema` → `2-api` → `3-frontend`. Do not start a later item until its dependencies are `Done`.
3. When starting a work item set `Status: In Progress`; when finished, tick its checklist, set `Status: Done`,
   and update the status column in `Product/README.md`.
4. If implementation diverges from the spec, update the feature doc / architecture doc in the same change.
5. Record unresolved questions in the feature's **Open questions** section rather than guessing.

## Conventions

- **Layout**: `Data/` (DbContext, entities, migrations), `Services/` (business logic, one service per aggregate),
  `Pages/<Area>/` (Razor Pages + `_Partial.cshtml` HTMX fragments). See architecture doc.
- **Business logic lives in services**, never in page handlers. Page handlers: bind → authorize → call service → render.
- **HTMX**: full page on normal request, partial on `HX-Request` header. Partials named `_Thing.cshtml`.
  Antiforgery token sent via `hx-headers` set in layout.
- **Money**: store as `long` minor units (cents), column suffix `Cents`. Never `decimal` in SQLite.
- **Time**: store UTC `DateTime`; convert to local only in views.
- **Stock**: quantity on hand changes **only** through `StockService` which writes a `StockTransaction` and
  updates `Product.QuantityOnHand` in the same DB transaction. Never edit or delete stock transactions; correct with a new one.
- **Soft delete** reference data (`IsActive = false`); never hard-delete rows referenced by transactions or POs.
- **Authorization**: policies `RequireAdmin`, `CanEdit` (Admin, Manager, Staff), `CanApprove` (Admin, Manager; deciding and cancelling submitted requisitions), `CanView` (all signed-in), `CanRequest` (all signed-in; the one write allowed to Viewers, raising and tracking their own requisitions, with ownership enforced by `RequisitionService`). Every page has `[Authorize(Policy = ...)]`.
- **Validation**: DataAnnotations on input models + service-level rule checks returning `Result`/errors rendered in forms.
- Nullable reference types on; warnings as errors; file-scoped namespaces; `async` all the way for DB calls.
- No new NuGet packages or JS libraries without noting it in Product/architecture.md → Decisions.
- **Navigation**: sections whose pages do not exist yet are listed in Pages/Shared/NavItems.cs with Available: false (rendered as inert labels, never links). The work item that adds a section's first page flips it to 	rue.
- **Auth in tests**: use AppFactory (X-Test-Role header selects Admin/Manager/Staff/Viewer without signing in); use CreateAnonymousClient() + WebForms.LoginAsync when the real cookie flow matters (session rejection, lockout, ReturnUrl).

## Definition of done

- Builds with zero warnings; `dotnet test` green.
- New services have unit/integration tests; new pages have at least one `WebApplicationFactory` smoke test.
- Migration added if schema changed and applies cleanly to an empty DB and to the previous schema.
- Role checks verified (Viewer cannot mutate, except their own requisitions through `CanRequest`).
- Docs + statuses updated.

## Don't

- Don't introduce a SPA framework, Node build, or external DB server.
- Don't enable trimming or Native AOT (EF Core + Razor are not AOT-safe).
- Don't commit `*.db` files or secrets.
