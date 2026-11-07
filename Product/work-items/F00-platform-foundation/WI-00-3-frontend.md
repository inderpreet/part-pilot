# WI-00-3 — Layout, HTMX conventions, shared partials

- **Feature:** [F00 — Platform foundation](../../Features/F00-platform-foundation.md)
- **Status:** Planned
- **Depends on:** WI-00-2
- **Estimate:** M

## Goal

A consistent page shell and reusable UI building blocks so feature pages only render their own content.

## Scope

- In: layout, nav, vendored assets, flash/toast, modal, pager, sortable header, form helpers, error pages, placeholder home.
- Out: feature pages; login (WI-01-3).

## Tasks

- [ ] Vendor `htmx.min.js` (2.x, pinned version noted in architecture) and `pico.min.css` into `wwwroot/lib/`.
- [ ] `wwwroot/css/site.css`: badges (low stock, status), table density, print styles, modal.
- [ ] `wwwroot/js/site.js`: listen for `flash` / `closeModal` HX-Trigger events; close `<dialog>`; focus first input in modals.
- [ ] `Pages/Shared/_Layout.cshtml`: header with company name (from settings), nav (Dashboard, Products, Stock, Suppliers,
      Purchase Orders, Reports, Admin), user menu slot, `#flash`, `#modal` `<dialog>`, `hx-headers` antiforgery on `<body>`.
- [ ] Nav items rendered per role (stub until F01; tag helper `asp-policy` or `@if (await Authz...)`).
- [ ] Partials: `_Flash`, `_Pager` (page, size, total, keeps query string), `_SortHeader`, `_ValidationSummary`, `_EmptyState`, `_ConfirmButton` (`hx-confirm`).
- [ ] `ListQuery` model (Search, Page, PageSize, Sort, Desc) + `PagedResult<T>` used by all lists.
- [ ] Money/date display helpers: `@Html.Money(cents)` using currency symbol setting; local time formatting.
- [ ] Error pages: `/Error`, `/Error/404`, `/Error/403` styled.
- [ ] Placeholder `Index` page ("Dashboard coming in F06").
- [ ] Style guide page `/Dev/Components` (Development only) showing all partials.

## Acceptance criteria

- [ ] Layout renders on phone width without horizontal scroll; tables scroll inside container.
- [ ] Triggering `HX-Trigger: {"flash":"Saved"}` shows a toast that auto-dismisses.
- [ ] Modal loads via `hx-get`, closes on `closeModal` trigger and on Escape.
- [ ] Antiforgery validation passes for an `hx-post` test form.

## Notes

See architecture §4. Keep markup semantic so Pico styles apply without classes.
