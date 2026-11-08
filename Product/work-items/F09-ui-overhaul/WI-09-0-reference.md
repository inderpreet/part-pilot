# WI-09-0 — Reference page

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-05-3
- **Estimate:** S

## Goal

A Development page shows the target UI on sample data, so every later work item has a picture to match. The real screens are unchanged by this item.

## Scope

- In: `/Dev/Overhaul`, its layout (`_OverhaulLayout`), `wwwroot/css/overhaul.css`, a link from `/Dev/Components`.
- Out: restyling the real shell, lists, forms or print view (WI-09-1 onward).

## Tasks

- [x] Page at `/Dev/Overhaul`, `[Authorize(CanView)]`, Development and Testing only (404 otherwise).
- [x] Own layout, so the prototype is the whole viewport and is not nested under the live chrome.
- [x] Four sample screens (dashboard, list, detail, form) switched from a bar at the top, with the rail, top bar, chips, buttons, overflow menu, confirm dialog, toast, field error and error summary.
- [x] Light rail; Reports as an inert "Soon" label.
- [x] Narrow layout: rail becomes a drawer below 64rem.
- [x] Tests: the page renders every screen in Testing; Production returns 404; a fresh install redirects to setup.

## Acceptance criteria

- [x] Signed in, in Development, `/Dev/Overhaul` shows all four screens and the Reports label is not a link.
- [x] Delete from the overflow menu opens the in-page confirm dialog, focuses Cancel, and does not post.
- [x] `/PurchaseOrders` is unchanged by this item.
- [x] `dotnet test` is green. No new package.

## Notes

The class names on this page (`app`, `rail`, `stage`, `topbar`, `page-head`, `card`, `btn`, `badge`, `toolbar`, `tabs`, `form-row`, `action-bar`, `alert`, `timeline`, `stat`) are the names the real screens use. WI-09-1 moves the rules from `overhaul.css` into `site.css`; WI-09-8 points this page at `site.css` and deletes `overhaul.css`.

### Retired

The page and `overhaul.css` were deleted in WI-09-8 once the live screens matched it. `/Dev/Components` is the gallery of the shared patterns now.
