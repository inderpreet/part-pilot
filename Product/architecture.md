# Architecture

## 1. Overview

A single ASP.NET Core process serves server-rendered HTML (Razor Pages) enhanced with HTMX, and persists to a
local SQLite file through EF Core. Everything — compiled code, Razor views, CSS, JS — ships inside one
self-contained executable.

```
 Browser (HTML + HTMX + Pico.css)
        │  HTTP (full pages / HTML partials on HX-Request)
        ▼
 ┌─────────────────────────── InventoryTracker.exe ───────────────────────────┐
 │  Kestrel                                                                    │
 │   ├─ Middleware: exception handler · static files (embedded) · auth · CSRF │
 │   ├─ Razor Pages (Pages/<Area>)  ── thin handlers: bind → authorize → call │
 │   ├─ Services (Services/*)       ── business rules, transactions           │
 │   └─ EF Core AppDbContext        ── entities, migrations, audit interceptor│
 └──────────────────────────────────────────┬──────────────────────────────────┘
                                            ▼
                                inventory.db (SQLite, WAL mode)
```

## 2. Technology choices

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 10 LTS | Long support window; mature single-file publish |
| UI | Razor Pages + HTMX 2.x | Server-rendered, no JS build step, small surface |
| Styling | Pico.css (vendored) + `site.css` | Clean defaults with semantic HTML, no build |
| ORM | EF Core 10 + SQLite provider | Migrations, LINQ, Identity integration |
| Auth | ASP.NET Core Identity (cookie) | Password hashing, lockout, roles out of the box |
| CSV | CsvHelper | Robust parsing/mapping |
| Tests | xUnit + `WebApplicationFactory` + SQLite `:memory:` | Real provider semantics in tests |

## 3. Solution layout

```
Inventory-Tracker/
├─ AGENTS.md / CLAUDE.md / .github/copilot-instructions.md
├─ Product/                         specs (this folder)
├─ scripts/publish.ps1              single-file publish for each RID
├─ src/InventoryTracker/
│  ├─ Program.cs                    host setup, DI, middleware, CLI args
│  ├─ appsettings.json
│  ├─ Data/
│  │  ├─ AppDbContext.cs
│  │  ├─ Entities/                  POCO entities (one file each)
│  │  ├─ Configurations/            IEntityTypeConfiguration<T> per entity
│  │  ├─ Interceptors/              AuditInterceptor, TimestampInterceptor
│  │  └─ Migrations/
│  ├─ Services/                     ProductService, StockService, ... + Result types
│  ├─ Pages/
│  │  ├─ Shared/                    _Layout, _Flash, _Pager, _ValidationSummary
│  │  ├─ Account/                   Login, Logout, Setup, ChangePassword
│  │  ├─ Admin/                     Users, Settings, Audit, Import, Backup
│  │  ├─ Products/ Categories/ Stock/ Suppliers/ PurchaseOrders/ Reports/
│  │  └─ Index.cshtml               dashboard
│  └─ wwwroot/                      css/, js/htmx.min.js, lib/pico.min.css (EmbeddedResource)
└─ tests/InventoryTracker.Tests/
   ├─ Services/                     service tests on SQLite in-memory
   └─ Pages/                        WebApplicationFactory smoke/authorization tests
```

## 4. Request flow and HTMX conventions

- A page handler returns the full page normally, or a partial when `Request.Headers["HX-Request"]` is present
  (helper: `this.IsHtmx()` → `Partial("_Rows", model)`).
- Lists: search box uses `hx-get` with `hx-trigger="input changed delay:300ms"` targeting the table body; paging and
  sort links use `hx-get` + `hx-push-url="true"` so URLs stay shareable.
- Forms: `hx-post` to the page handler; on validation error return the form partial with errors (HTTP 422);
  on success return `HX-Redirect` or an updated fragment plus `HX-Trigger: {"flash": "..."}` for a toast.
- Antiforgery: layout sets `hx-headers='{"RequestVerificationToken": "@token"}'` on `<body>`.
- Modals: `<dialog>` element loaded via `hx-get` into `#modal`, closed on `HX-Trigger: closeModal`.
- Without JS the app degrades to normal form posts/links where practical.

## 5. Data model

All tables have `Id INTEGER PRIMARY KEY`. Audited/mutable entities have `CreatedAt`, `UpdatedAt` (UTC) set by
`TimestampInterceptor`. Money columns are `long` minor units (`...Cents`). Quantities are `int` (whole units) in v1.

```
AppSetting(Key PK, Value)

AspNetUsers (Identity, extended: DisplayName, IsActive, LastSignInAt)  AspNetRoles: Admin, Staff, Viewer

Category(Id, Name UQ, Description, IsActive)
Product(Id, Sku UQ, Name, Description, CategoryId→Category?, Unit, Barcode UQ?, BinLocation,
        ReorderPoint, ReorderQuantity, UnitCostCents, QuantityOnHand, IsActive, RowVersion)

StockTransaction(Id, ProductId→Product, Type, QuantityDelta, QuantityAfter, UnitCostCents?,
                 Reason, Reference, Note, PurchaseOrderLineId→PurchaseOrderLine?, ImportBatchId→ImportBatch?,
                 ReversesTransactionId→StockTransaction?, UserId→AspNetUsers, OccurredAt, CreatedAt)
  Type ∈ {Receive, Issue, Adjust, Count, Return, Reversal}

Supplier(Id, Name UQ, ContactName, Email, Phone, Address, Website, Notes, IsActive)
ProductSupplier(ProductId, SupplierId PK pair, SupplierSku, UnitCostCents, LeadTimeDays, MinOrderQuantity, IsPreferred)

PurchaseOrder(Id, Number UQ, SupplierId→Supplier, Status, OrderDate, ExpectedDate, SubmittedAt, ClosedAt,
              Notes, CreatedById→AspNetUsers, RowVersion)
  Status ∈ {Draft, Submitted, PartiallyReceived, Received, Cancelled}
PurchaseOrderLine(Id, PurchaseOrderId→PurchaseOrder, ProductId→Product, QuantityOrdered, QuantityReceived, UnitCostCents)
DocumentSequence(Name PK, Year, NextValue)          -- PO numbering

ImportBatch(Id, Kind, FileName, RowCount, CreatedCount, UpdatedCount, SkippedCount, CreatedById, CreatedAt)

AuditEntry(Id, OccurredAt, UserId?, UserName, Action, EntityType, EntityId, ChangesJson, IpAddress)
```

Relationships: Category 1–* Product; Product 1–* StockTransaction; Product *–* Supplier via ProductSupplier;
Supplier 1–* PurchaseOrder 1–* PurchaseOrderLine *–1 Product; PurchaseOrderLine 1–* StockTransaction (receipts).

Derived values (not stored): **on order** = Σ(QuantityOrdered − QuantityReceived) over lines of Submitted /
PartiallyReceived POs; **stock value** = Σ(QuantityOnHand × UnitCostCents); **low stock** =
`QuantityOnHand + OnOrder <= ReorderPoint AND ReorderPoint > 0 AND IsActive`.

## 6. Key invariants

1. `Product.QuantityOnHand` always equals Σ `StockTransaction.QuantityDelta` for that product. Only
   `StockService` writes both, inside one DB transaction. A consistency check (admin action + test) recomputes it.
2. Stock transactions are immutable. Mistakes are corrected with a `Reversal` transaction pointing at the original.
3. Negative on-hand is rejected unless setting `Stock.AllowNegative = true`.
4. PO status transitions are enforced by `PurchaseOrderService` (see F05); received quantities only increase via receiving.
5. Optimistic concurrency on `Product` and `PurchaseOrder` via `RowVersion` (incremented in `SaveChanges`, since SQLite has no rowversion type).

## 7. SQLite configuration

- Connection string from config; default file `inventory.db` in the executable's directory (`AppContext.BaseDirectory`), override with `--db <path>` or `ConnectionStrings__Default`.
- On open: `PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=NORMAL;` (via a connection interceptor).
- Migrations applied automatically at startup (`Database.MigrateAsync()`), after taking a file copy backup
  `inventory.db.bak-<timestamp>` when pending migrations exist.
- Case-insensitive uniqueness for SKU / names via `COLLATE NOCASE` columns.
- Backup download uses `VACUUM INTO` to a temp file to get a consistent snapshot while running.

## 8. Authentication and authorization

- ASP.NET Core Identity (`AddIdentityCore` + EF stores + `SignInManager`) with cookie auth. The cookie scheme is registered
  with `AddIdentityCookies`: the security-stamp validator signs out of Identity's other schemes when it rejects a session,
  so all of them must exist (registering only the application cookie turns a rejected session into a 500).
- Passwords: min 8, max 128, no complexity rules. Lockout after 5 failed attempts for 15 min. Usernames unique, case-insensitive.
- Cookie `.Inventory.Auth`: `HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest` (the app speaks HTTP; see open questions),
  8 h sliding. "Remember me" gives a persistent 14-day sliding cookie; otherwise it is a session cookie.
- First run: if no users exist, every request except static files, `/Account/Setup`, `/health` and `/Error*` redirects to
  `/Account/Setup` (`HX-Redirect` for htmx). The "users exist" answer is cached once true. After the first Admin exists the page is 404.
- Policies: `RequireAdmin` (Admin), `CanEdit` (Admin, Staff), `CanView` (any authenticated active user). Every policy **and**
  the fallback policy require an authenticated user carrying the `inv:active` claim. Applied with
  `[Authorize(Policy = ...)]` on page models; a page that names a policy is not evaluated against the fallback, so each policy
  repeats the active check.
- Sessions are re-validated every `Auth:ValidationIntervalSeconds` (default 60). `ActiveUserSecurityStampValidator` rejects
  deactivated users. The security stamp rotates on role change, deactivation and any password change/reset, which ends other sessions.
- Business rules live in `UserService` (last active Admin cannot be demoted/deactivated; one role per user). Admin-affecting
  mutations and first-run setup are serialised by a static semaphore so concurrent requests cannot race past the last-admin guard.
- htmx and expired sessions: a request with `HX-Request` and no valid session gets `401` + `HX-Redirect` to
  `/Account/Login?ReturnUrl=<page being viewed>` (same-host `HX-Current-URL` only); an authenticated user lacking the role gets `403`, no redirect.
  `ReturnUrl` is honoured only if it is a local path and not a login URL.
- Sign out is POST-only and antiforgery-protected. It is `[AllowAnonymous]` so a deactivated user can still sign out.
- Sign-in events are published through `IUserEvents` (fan-out publisher that logs handler failures) for the F08 audit log.
- No self-registration; Admin creates users and sets initial passwords.
- Data-protection keys persist to `<db dir>/keys` (D12) so sign-in cookies and antiforgery tokens survive restarts.
  The keys are not encrypted at rest (ASP.NET logs a warning); protect the folder like the database.
- Code layout: `Auth/` (claims, validators, policies, `ICurrentUser`, first-run), `Services/UserService`, `Services/SignInService`.
## 9. Configuration and CLI

| Setting | Source | Default |
|---|---|---|
| Who can connect | `--bind`, `INVENTORY_BIND` | `localhost` (loopback only). `all` / `lan` / `0.0.0.0` = every interface (tablets on the LAN); or a specific IP |
| Port | `--port`, `INVENTORY_PORT` | `5080` |
| Listen URL (full override of bind + port) | `--urls`, `ASPNETCORE_URLS` | derived: `http://<bind>:<port>` |
| DB path | `--db`, `INVENTORY_DB`, `ConnectionStrings__Default` | `<exe dir>/inventory.db` |
| Open browser on start | `--open` | off |
| File logging | `Logging__File__Enabled`, `Logging__File__Directory` | on, `<db dir>/logs/` |
| Session re-validation interval | `Auth__ValidationIntervalSeconds` | `60` |
| Data-protection keys | derived from DB path | `<db dir>/keys/` |
| Business settings (company name, currency, allow negative stock, PO number prefix) | `AppSetting` table via Admin → Settings | see F00 |

**Precedence** (highest first): command line › real environment variables › `.env` file › optional `appsettings.json`
beside the executable › built-in defaults. The built-in defaults live in code (nothing needs to sit beside the executable).

**`.env` file**: simple `KEY=VALUE` lines, read from the working directory and then from the executable's directory
(first file wins; real environment variables are never overridden). See `.env.example` at the repo root. To let
tablets on the LAN connect, set `INVENTORY_BIND=all` (and allow the port through the OS firewall). The app logs a
warning at startup whenever it is bound beyond loopback, because the app speaks plain HTTP (see HTTPS open question).

## 10. Packaging (single binary)

```
dotnet publish src/InventoryTracker -c Release -r <rid> --self-contained true
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
  -p:EnableCompressionInSingleFile=true -p:DebugType=none
```

- RIDs: `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`.
- Razor views are compiled into the assembly. `wwwroot` is included as embedded resources and served with
  `ManifestEmbeddedFileProvider` (`GenerateEmbeddedFilesManifest=true`), so no loose files are needed.
- SQLite native library (`e_sqlite3`) is bundled and self-extracted.
- Trimming and Native AOT are **disabled** (EF Core/Razor reflection). Measured single-file sizes (compressed, v0.1.0): win-x64 **51.1 MB**, linux-x64 52.1 MB, linux-arm64 49.6 MB, osx-arm64 49.1 MB.
- StaticWebAssetsEnabled=false and IsTransformWebConfigDisabled=true keep publish from emitting loose manifest/web.config files; scripts/publish.ps1 fails unless the output folder holds exactly one file. scripts/smoke.ps1 runs the exe alone against a temp DB.
- Default logging config and AllowedHosts are built into the code (lowest-priority in-memory source); there is no ppsettings.json.
- Optional: run as Windows Service / systemd unit (`UseWindowsService()` / `UseSystemd()` enabled; docs only).

## 11. Error handling and logging

- `UseExceptionHandler("/Error")`; HTMX requests get a small error partial + `HX-Retarget` to the flash area.
- Services return `Result`/`Result<T>` for expected business failures; exceptions are for bugs/infrastructure.
- Logging: console + rolling file `logs/` beside the DB (Microsoft.Extensions.Logging, simple file provider).

## 12. Testing strategy

- **Service tests**: real SQLite in-memory connection kept open per test; migrations applied; cover invariants.
- **Page tests**: `WebApplicationFactory` with test auth handler per role; assert status codes, role enforcement, HTMX partial vs full response.
- **Packaging smoke test** (CI/manual): publish win-x64, start exe with temp DB, GET `/health` returns 200.

## 13. Decisions log

| # | Decision | Rationale |
|---|---|---|
| D1 | ASP.NET Core over Go/Rust | Productivity (Identity, EF migrations, Razor); accepted larger binary |
| D2 | Server-rendered + HTMX over SPA | No Node toolchain, simpler single-binary embedding |
| D3 | Money as `long` cents | EF Core SQLite stores `decimal` as TEXT; cannot sum/sort server-side |
| D4 | Cached `QuantityOnHand` + immutable ledger | Fast reads, full history, verifiable |
| D5 | Single location, `BinLocation` as free text on Product | v1 scope; multi-location would add `Location` + per-location stock table |
| D6 | No trimming/AOT | EF Core and Razor Pages not AOT-compatible |
| D7 | Listen address configured via `.env`/env vars/CLI (`INVENTORY_BIND`, default `localhost`) | Safe-by-default; one switch to open the app to LAN devices without rebuilding |
| D8 | NuGet: `Microsoft.Extensions.FileProviders.Embedded` | Generates the embedded-files manifest so `wwwroot` is served from inside the executable |
| D9 | NuGet: `Microsoft.Extensions.Hosting.WindowsServices`, `Microsoft.Extensions.Hosting.Systemd` | Run as a Windows Service / systemd unit; both are no-ops when launched normally |
| D10 | NuGet (tests only): `Microsoft.AspNetCore.Mvc.Testing` | `WebApplicationFactory` for page/host smoke tests |
| D11 | Vendored front-end assets pinned: htmx **2.0.11**, Pico CSS **2.1.1** (`wwwroot/lib/`, copied from the npm packages via jsDelivr) | No Node build; versions recorded here so upgrades are deliberate. htmx runs with `allowEval=false` / `selfRequestsOnly=true` |
| D12 | Data-protection keys persisted to `<db dir>/keys` (file system, unencrypted) | Without a persisted key ring every restart invalidates sign-in cookies and antiforgery tokens (and a single-file app has no stable default location). Same trust boundary as the DB file: whoever can read the folder already owns the data |
| D13 | NuGet: `Microsoft.AspNetCore.Identity.EntityFrameworkCore` (+ `Microsoft.EntityFrameworkCore.Design`, build-time only, `PrivateAssets=all`) | Identity stores on EF Core; Design is only for `dotnet ef migrations` |
| D14 | The stock ledger is made immutable by the database: SQLite `BEFORE UPDATE` / `BEFORE DELETE` triggers on `StockTransactions` (`RAISE(ABORT)`), plus CHECKs (`QuantityDelta <> 0`, `Type` in the enum, `Type = 'Reversal'` ⇔ `ReversesTransactionId IS NOT NULL`) and a filtered unique index on `ReversesTransactionId` | Invariant 2 holds even for code or SQL that bypasses `StockService`. The triggers are hand-written in the `AddStockLedger` migration (EF cannot model them); a future migration that rebuilds the table must re-create them (`StockSchemaTests` fails if it forgets) |
| D15 | `StockService` writes `Product.QuantityOnHand` with a guarded `ExecuteUpdate` (`WHERE QuantityOnHand = <value read>`) inside a `BEGIN IMMEDIATE` transaction, not through the change tracker | A stock movement must not bump `RowVersion`/`UpdatedAt`: otherwise every sale would make an open product edit form fail with a false conflict. `StockInvariantSourceTests` fails if any other production file writes the column |

## 14. Open questions

- Is a JSON API (`/api/v1`) needed for integrations (e.g. e-commerce sync)? Currently out of scope.
- Fractional quantities (kg, m)? v1 assumes whole units; changing later means `int` → scaled `long`.
- HTTPS: run behind a reverse proxy, or support a self-signed/PFX cert via config?
- Currency: single currency per install assumed.
