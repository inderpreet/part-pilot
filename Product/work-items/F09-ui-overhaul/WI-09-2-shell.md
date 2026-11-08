# WI-09-2 — Shell: rail, top bar, skip link, drawer

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-1
- **Estimate:** M

## Goal

Signed-in pages have the left rail, the top bar and a skip link. Anonymous pages have none of them. On narrow screens the rail is a drawer.

## Scope

- In: `_Layout.cshtml`, `NavItems.cs` (group and icon per item), `site.css`, `site.js` (drawer), `LayoutAndComponentTests`.
- Out: page headers (WI-09-3). Global search. Changing routes or policies.

## Tasks

- [x] `NavItem` gains `Group` (none, "Inventory", "Insights") and `Icon` (a symbol id). `NavItems.All` assigns them. Admin renders in the rail foot, not in a group. Existing members keep their order and policies.
- [x] `_Layout`: skip link first; `div.app` with `aside.rail` and `div.stage` (top bar, flash, `main#content`, footer). The icon sprite is a partial (`_Icons.cshtml`) with the symbols from `/Dev/Overhaul`.
- [x] Rail items: `Available` is a link with `aria-current="page"` on the current section; not available is `li.rail-soon` with `<span aria-disabled="true" title="Coming soon">Label</span>` and a "Soon" tag. Hidden entirely when `navItems` is empty (anonymous).
- [x] Rail foot: Admin link (when the user may see it) and the user card with avatar initials, name, role, a "Change password" link and a Sign out form (post).
- [x] Top bar: menu button (visible below 64rem, `aria-controls="rail"`), breadcrumb showing the current section label. Anonymous layout: a plain centred layout with the company name, no rail, no top bar.
- [x] `site.js`: drawer open/close (menu button, scrim, Escape, link click). The `js` class is set on `<html>` in `<head>` so that without JavaScript the rail stays visible.
- [x] Tests: signed-in pages contain `href="#content"` and `<main id="content"`; Reports is an inert span and not a link; Purchase Orders is a link with `aria-current="page"` on `/PurchaseOrders`; a Viewer does not see Admin; anonymous login page has no `class="rail"`.

## Acceptance criteria

- [x] Tab once from the top of a signed-in page: focus lands on "Skip to content"; activating it moves focus to `main`.
- [x] The rail shows Dashboard, Products, Stock, Suppliers, Purchase Orders (links) and Reports (inert "Soon"); Admin for an admin only.
- [x] Below 64rem the menu button opens a drawer that closes on Escape, scrim click and link click.
- [x] Login, Setup, Error and AccessDenied pages show no rail.
- [x] `dotnet test` is green. No new package.

## Notes

FR-2, FR-10 (drawer). Mirror the markup of `/Dev/Overhaul` so the CSS is shared. Don't add a global search.
