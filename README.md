# Inventory Tracker

Self-hosted inventory tracking for a small business with a **single stock location**. It ships as one
self-contained executable: run it, open the browser, sign in. Data lives in a single SQLite file.

> **Status: early development.** The platform foundation (F00) and sign-in/user management (F01) are done; catalog,
> stock and the rest are specified but not built yet. See [Project status](#project-status).

## Features (planned)

- Product catalog with SKUs, categories, reorder points and costs
- Immutable stock ledger: receive, issue, adjust, count - with an always-verifiable on-hand quantity
- Suppliers, purchase orders and receiving into stock
- Dashboard, low-stock alerts, reorder suggestions and reports
- CSV import/export and one-click database backup
- Roles (Admin / Staff / Viewer) and an audit log

Full specs live in [`Product/`](Product/README.md).

## Tech stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS), C# |
| Web | ASP.NET Core Razor Pages + [htmx](https://htmx.org) (vendored, no Node build) |
| CSS | [Pico.css](https://picocss.com) (vendored) + `site.css` |
| Data | EF Core + SQLite (WAL mode) |
| Auth | ASP.NET Core Identity, cookie auth, roles |
| Tests | xUnit, `WebApplicationFactory`, SQLite in-memory |
| Packaging | `dotnet publish` self-contained single file, static assets embedded |

## Quick start

Prerequisites: the [.NET 10 SDK](https://dotnet.microsoft.com/download) and PowerShell (Windows PowerShell 5.1 or
PowerShell 7+).

```powershell
.\start.ps1
```

That builds and runs the app, then opens <http://localhost:5080>. Stop it with `Ctrl+C`.
The database is created automatically at `.data\inventory.db` on first run.

Without the script:

```powershell
dotnet run --project src/InventoryTracker
```

(In that case the database is created next to the build output unless you set `INVENTORY_DB`; see below.)

### First run and signing in

A fresh install has no users, so every page redirects to **/Account/Setup**. Enter your company name and create the
administrator account (password: at least 8 characters). You are signed in automatically.

From **Admin > Users** the administrator creates the other accounts and picks a role for each:

| Role | Can do |
|---|---|
| Admin | Everything, plus users and settings |
| Staff | Create and edit data, record stock movements |
| Viewer | Read-only |

There is no self-registration and no password-reset email: an Admin resets a forgotten password from the user's edit page.
Five wrong passwords lock an account for 15 minutes. Deactivating a user ends their session on their next request.

### Start options

```powershell
.\start.ps1 -Lan            # let other devices on your network connect (tablets at the dock)
.\start.ps1 -Port 6000      # different port
.\start.ps1 -NoOpen         # don't open the browser
.\start.ps1 -Watch          # restart automatically when code changes
.\start.ps1 -DbPath D:\data\inventory.db
```

## Configuration

The app listens on **this machine only** by default. Settings can come from the command line, environment variables
or a `.env` file (copy [`.env.example`](.env.example) to `.env`):

| Setting | CLI | Env / `.env` | Default |
|---|---|---|---|
| Who can connect | `--bind <localhost\|all\|ip>` | `INVENTORY_BIND` | `localhost` |
| Port | `--port <n>` | `INVENTORY_PORT` | `5080` |
| Database file | `--db <path>` | `INVENTORY_DB` | `inventory.db` next to the executable |
| Open browser on start | `--open` | - | off |
| Full URL override | `--urls <url>` | `ASPNETCORE_URLS` | derived from bind + port |

Precedence, highest first: command line, real environment variables, `.env`, `appsettings.json` beside the executable,
built-in defaults.

> **Network exposure:** `all` / `-Lan` makes the app reachable by other machines over **plain HTTP**. Only use it on a
> trusted network and allow the port in your firewall. The app logs a warning whenever it is bound beyond localhost.

Health check: `GET /health` returns `{ "status", "version", "db" }` (HTTP 503 if the database is unreachable).
Logs are written to a `logs/` folder next to the database file.

## Building and running the single executable

```powershell
.\scripts\publish.ps1                       # this machine's platform, e.g. publish\win-x64\InventoryTracker.exe
.\scripts\publish.ps1 -Runtime linux-x64    # also: linux-arm64, osx-arm64
.\scripts\publish.ps1 -All                  # every platform
.\scripts\publish.ps1 -Smoke                # also start the exe in an empty folder and check it works
```

(`scripts/publish.sh` / `scripts/smoke.sh` do the same from a shell.) Each result is **one file**, about 50 MB, with
the .NET runtime, SQLite and all web assets inside; the script fails if the output folder holds anything else.
Copy it anywhere and run it:

```powershell
.\InventoryTracker.exe                          # http://localhost:5080, inventory.db next to the exe
.\InventoryTracker.exe --bind all --port 8080 --db D:\data\inventory.db
```

Settings are the same as in [Configuration](#configuration). On first run it creates `inventory.db`, `logs/` and `keys/`
(the key ring that keeps people signed in across restarts) next to the database file.

> Verified so far: the win-x64 build passes the smoke test. The Linux and macOS binaries build but have not been run,
> and the `.sh` scripts have not been executed. The exe has not been tried on a machine without .NET installed (it is
> self-contained, so it should work).

### Running as a service

The host supports both; these are starting points, not yet tested end to end.

```powershell
# Windows Service (run from an elevated prompt)
sc.exe create InventoryTracker binPath= "C:\InventoryTracker\InventoryTracker.exe --db C:\InventoryTracker\data\inventory.db" start= auto
sc.exe start InventoryTracker
```

```ini
# /etc/systemd/system/inventory-tracker.service
[Unit]
Description=Inventory Tracker
After=network.target

[Service]
ExecStart=/opt/inventory-tracker/InventoryTracker --db /var/lib/inventory-tracker/inventory.db
User=inventory
Restart=on-failure

[Install]
WantedBy=multi-user.target
```

### Backup and restore

Everything lives in the folder that holds the database file. To back up, **stop the app** and copy `inventory.db`
(plus the `keys/` folder if you want people to stay signed in after a restore). To restore, put them back and start the app.
Do not copy the database while the app is running: it uses write-ahead logging, so the `.db` file alone may be missing recent changes.
A one-click backup download is planned (F07).

Treat the folder like the database itself: the `keys/` folder is not encrypted.

## Development
[`scripts/dev.ps1`](scripts/dev.ps1) wraps the everyday commands (`.\scripts\dev.ps1 help` for details):

```powershell
.\scripts\dev.ps1 run                      # run the app
.\scripts\dev.ps1 watch                    # run with auto-restart
.\scripts\dev.ps1 test                     # all tests
.\scripts\dev.ps1 test -Filter "FullyQualifiedName~Settings"
.\scripts\dev.ps1 build                    # build (warnings are errors)
.\scripts\dev.ps1 migrate -Name AddProducts
.\scripts\dev.ps1 reset-db                 # delete the dev database
```

Or use the plain commands:

```powershell
dotnet build
dotnet test
dotnet ef migrations add <Name> --project src/InventoryTracker --output-dir Data/Migrations
```

A style guide for the shared UI components is available at `/Dev/Components` when running in Development.

### Repository layout

```
Inventory-Tracker/
├─ start.ps1                    start the app
├─ scripts/dev.ps1              developer tasks
├─ scripts/publish.ps1          build the single-file executable (+ smoke.ps1)
├─ src/InventoryTracker/
│  ├─ Program.cs                host setup, config, middleware
│  ├─ Data/                     DbContext, entities, configurations, interceptors, migrations
│  ├─ Auth/                     cookie auth, policies, current user, first-run redirect
│  ├─ Services/                 business logic (one service per aggregate) and shared result/paging types
│  ├─ Hosting/                  CLI/.env/config, health check, logging, htmx and display helpers
│  ├─ Pages/                    Razor Pages, shared layout and partials
│  └─ wwwroot/                  css, js, vendored htmx + Pico (embedded into the assembly)
├─ tests/InventoryTracker.Tests/
└─ Product/                     feature specs, work items, architecture
```

### Conventions (short version)

Business logic lives in services, not page handlers. Money is stored as integer cents, time as UTC, and stock
quantity changes only through the stock service via immutable ledger rows. Nullable reference types are on and
warnings are errors. The full rules - and how work is tracked through work items - are in
[`AGENTS.md`](AGENTS.md) and [`Product/architecture.md`](Product/architecture.md).

## Project status

| Feature | Status |
|---|---|
| F00 Platform foundation | Done (Linux/macOS binaries and a .NET-free Windows machine still to be verified) |
| F01 Users & authentication | Done |
| F02 Product catalog | Planned |
| F03 Stock tracking | Planned |
| F04 Suppliers | Planned |
| F05 Purchase orders & receiving | Planned |
| F06 Dashboard, alerts & reports | Planned |
| F07 Data import/export & backup | Planned |
| F08 Audit log | Planned |

The authoritative status table is in [Product/README.md](Product/README.md).

## Documentation

- [Product overview and feature index](Product/README.md)
- [Architecture and design decisions](Product/architecture.md)
- [Feature specs](Product/Features/) and [work items](Product/work-items/)
- [Agent / contributor instructions](AGENTS.md)
