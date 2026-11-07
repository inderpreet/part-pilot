# Copilot instructions

Follow `AGENTS.md` at the repo root — it is the single source of truth for stack, conventions, and workflow.

Key rules (summary):
- ASP.NET Core Razor Pages + HTMX, EF Core SQLite, .NET 10. Ships as one self-contained executable.
- Business logic in `Services/`, not in page handlers.
- Money as `long` cents; times stored UTC.
- Stock quantity changes only via `StockService` (writes ledger + updates cached quantity in one transaction).
- Every page has an authorization policy (`RequireAdmin`, `CanEdit`, `CanView`).
- Specs live in `Product/` — read the work item and feature spec before coding; update statuses when done.
