# WI-00-3 — Layout, HTMX conventions, shared partials

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Done
- **Depends on:** WI-00-2
- **Estimate:** M

## Goal

A consistent page shell and reusable UI building blocks so feature pages only render their own content.

## Scope

- In: layout, nav, vendored assets, flash/toast, modal, pager, sortable header, form helpers, error pages, placeholder home.
- Out: feature pages; login (WI-01-3).

## Tasks

- [x] Vendor `htmx.min.js` (2.x, pinned version noted in architecture) and `pico.min.css` into `wwwroot/lib/`.
- [x] `wwwroot/css/site.css`: badges (low stock, status), table density, print styles, modal.
- [x] `wwwroot/js/site.js`: listen for `flash` / `closeModal` HX-Trigger events; close `<dialog>`; focus first input in modals.
- [x] `Pages/Shared/_Layout.cshtml`: header with company name (from settings), nav (Dashboard, Products, Stock, Suppliers,
      Purchase Orders, Reports, Admin), user menu slot, `#flash`, `#modal` `<dialog>`, `hx-headers` antiforgery on `<body>`.
- [x] Nav items rendered per role (stub until F01; tag helper `asp-policy` or `@if (await Authz...)`).
- [x] Partials: `_Flash`, `_Pager` (page, size, total, keeps query string), `_SortHeader`, `_ValidationSummary`, `_EmptyState`, `_ConfirmButton` (`hx-confirm`).
- [x] `ListQuery` model (Search, Page, PageSize, Sort, Desc) + `PagedResult<T>` used by all lists.
- [x] Money/date display helpers: `@Html.Money(cents)` using currency symbol setting; local time formatting.
- [x] Error pages: `/Error`, `/Error/404`, `/Error/403` styled.
- [x] Placeholder `Index` page ("Dashboard coming in F06").
- [x] Style guide page `/Dev/Components` (Development only) showing all partials.

## Acceptance criteria

- [x] Layout renders on phone width without horizontal scroll; tables scroll inside container.
- [x] Triggering `HX-Trigger: {"flash":"Saved"}` shows a toast that auto-dismisses.
- [x] Modal loads via `hx-get`, closes on `closeModal` trigger and on Escape.
- [x] Antiforgery validation passes for an `hx-post` test form.

## Notes

See architecture §4. Keep markup semantic so Pico styles apply without classes.

### Implementation notes

- **Vendored (pinned):** htmx **2.0.11** and Pico CSS **2.1.1** in `wwwroot/lib/` (recorded in architecture §13, D11). To upgrade, replace the files and update that row. htmx runs with `allowEval:false` and `selfRequestsOnly:true` (`<meta name="htmx-config">` in the layout), so `hx-on`/`js:` expressions are not available.
- **Antiforgery:** layout puts the token in `hx-headers` on `<body>`; the app configures the header name `RequestVerificationToken`. Verified by tests (valid token = 200, missing = 400 with a flash-targeted error partial).
- **Flash:** `this.Flash(msg)` in a handler sends `HX-Trigger` for htmx requests or TempData for normal ones; `site.js` also auto-dismisses anything placed in `#flash` (toasts: 4 s, errors: 8 s, click to dismiss).
- **Errors in htmx:** `site.js` swaps 422 responses (validation) and any error response carrying `HX-Retarget` (the shared error partial); other 4xx/5xx show a generic error toast instead of silently doing nothing. This resolves the WI-00-2 note.
- **Service errors -> forms:** `ModelState.AddErrors(result)` maps `Error.Field` to ModelState keys; `_ValidationSummary` renders them.
- **Helpers:** `@Html.Money(cents)` (currency symbol from settings; reads the cached settings snapshot synchronously), `@Html.LocalTime(utc)` / `LocalDate` (renders `<time data-local>`: server-local text, upgraded to the browser's time zone by `site.js`).
- **Caching:** every response carries `Vary: HX-Request` because pages return a partial or a full page depending on that header.
- **Nav:** `NavItems.IsVisible` shows everything until F01; it must become an `IAuthorizationService` check against each item's policy in WI-01-3. The index page and others have no `[Authorize]` yet for the same reason (policies arrive in WI-01-2). Nav links to features that don't exist yet 404 until those features land.
- **Dev style guide:** `/Dev/Components` is available in Development and the `Testing` environment only (404 otherwise); it hosts the antiforgery/flash/modal/422 demos the tests use.
- **Verified in a real browser** (Development run): toast shows and auto-dismisses; modal opens via `hx-get`, focuses its input, and closes on Escape, backdrop/close controls, and `closeModal`; 422 swapped in place; at 390 px wide the page does not scroll horizontally (nav scrolls within itself, wide tables scroll within `.table-wrap`). Unit/page tests cover the server side (96 tests total).
