# WI-00-5 — Branding from configuration

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Done
- **Depends on:** WI-00-2, WI-00-3
- **Estimate:** S

## Goal

The product name ("Part Pilot") and the default company name ("Anhad Tech") come from configuration instead of being typed into pages, so a rename or a new install needs no code change.

## Scope

- In: a `Branding` configuration section, a resolved `BrandingSettings`, using it in the layout, first-run setup page and startup log, the company-name default, docs, tests.
- Out: a logo or colour theme; renaming internal identifiers (namespace `InventoryTracker`, executable name, cookie `.Inventory.Auth`, `inventory.db`, data-protection application name). Renaming those would sign everyone out or orphan existing databases, so they stay.

## Tasks

- [x] `BrandingSettings(ProductName, CompanyName)` in `Hosting/`, resolved from `Branding:ProductName` and `Branding:CompanyName` (command line, environment `Branding__ProductName`, `.env`, `appsettings.json`), with built-in defaults `Part Pilot` and the seeded `My Company`; values are trimmed, blank means default, more than 100 characters or control characters is a startup error naming the key.
- [x] Registered as a singleton (resolved lazily, like `HostSettings`).
- [x] Layout: browser tab title = `<page> · <product>`, footer = `<company> · <product> v<version>`, error-page fallback name from the configured company. First-run setup page title = `Set up <product>`. Startup log line starts with the product name.
- [x] `SettingsService` shows the configured company when the stored company name is still the seeded placeholder; any name saved in Admin → Settings wins. The database row is never rewritten by this.
- [x] `.env.example` documents both keys; README and architecture configuration tables list them.
- [x] Tests: `BrandingSettingsTests` (defaults, override, trim, blank, limits, control characters, `.env` line with spaces); `SettingsServiceTests` (placeholder replaced, saved name wins, other settings untouched, no branding = unchanged); `BrandingPagesTests` (signed-in pages, sign-in page, first-run setup, Admin → Settings prefill, encoding, defaults); `AppFactory` pins the branding so a developer's own environment never changes test results.

## Acceptance criteria

- [x] With `Branding__ProductName=Part Pilot` and `Branding__CompanyName=Anhad Tech` the tab title, footer, rail brand and setup page show those names and the words "Inventory Tracker" appear nowhere in the UI.
- [x] With no configuration at all the app runs and shows "Part Pilot" and "My Company".
- [x] A company name saved in Admin → Settings is shown instead of the configured default.
- [x] Builds with zero warnings; `dotnet test` green.

## Notes

- The product name is the product's identity, so it is not editable in the UI. The company name is the business using the app (it also prints on purchase orders), so Admin → Settings stays the place to change it after install; the configured value is only the starting default.
- A company literally named "My Company" cannot be told apart from "never set" and shows the configured default instead. That is accepted.

## As built

- **Files:** `Hosting/BrandingSettings.cs`; `Program.cs` (registration, startup log); `Services/SettingsService.cs` (optional `BrandingSettings` constructor argument, applied when the cache is built); `Pages/Shared/_Layout.cshtml`; `Pages/Account/Setup.cshtml`.
- **Print sheets** keep the company name in their tab title (a purchase order PDF should carry the business name, not the product).
