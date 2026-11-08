# WI-09-1 — Foundation: tokens, type, controls, chips, tables

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-0
- **Estimate:** M

## Goal

Every existing page picks up the new colours, type, controls, chips and tables from `site.css`, with no markup change. The old header and layout are still in place; they just look better.

## Scope

- In: `wwwroot/css/site.css` only (plus a small test).
- Out: `_Layout` markup (WI-09-2), page markup, `site.js`.

## Tasks

- [x] Add the tokens from FR-1 on `:root`: colour (`--it-canvas`, `--it-card`, `--it-line`, `--it-ink`, `--it-muted`, `--it-accent`, the four status pairs), radius, shadow, the `--it-text-*` scale. Keep `--it-ok/--it-warn/--it-danger/--it-info/--it-muted` names so current rules keep working.
- [x] Pin `--pico-font-size: 100%` on `:root`. Set `--pico-line-height`, `--pico-border-radius: 0.5rem`, `--pico-primary*`, form spacing.
- [x] Body on the canvas colour; headings use the title scale; `.num` and money use tabular numbers.
- [x] Controls: text inputs, selects and textareas 2.5rem high, 1px `--it-line-strong` border, accent focus ring; `aria-invalid="true"` shows a danger border and no Pico icon. Search inputs lose Pico's pill shape.
- [x] Buttons: 2.25rem high, `--it-text-sm`, 600. Default is primary. `.secondary`, `.outline` and `.secondary.outline` are white with a border (secondary). `.text` is borderless. `.danger` is solid danger. Natural width in `.form-actions` and toolbars as today. The rules do not touch `.linklike`, `.star`, `.move-button`, `.picker-item`.
- [x] Chips (FR-9): restyle `.badge` and its modifiers, add `.badge-overdue`. Class names unchanged.
- [x] Tables: 0.875rem, 1.3 line height, hairline row borders, a quiet header row, hover row, `striped` has no stripes. `.table-wrap` gets the card look (border, radius, white) so lists look right before WI-09-4.
- [x] Add the shared component classes the later items and F06 use: `.card`, `.card-head`, `.card-title`, `.card-foot`, `.stat*`, `.timeline`, `.alert`, `.meter`, `.kv`.
- [x] Add `:focus-visible` outlines (2px, offset 2px, accent) for `button.linklike`, `button.star`, `.move-button`, `.picker-list .picker-item`.
- [x] Test: `/css/site.css` is served and contains `--pico-font-size: 100%`.

## Acceptance criteria

- [x] Every existing page renders unchanged in structure and is visibly in the new palette, type and controls.
- [x] At 1440px wide, body text is 16px and table text is 14px (no 125% root).
- [x] The picker, star, move buttons, flash toasts and modal still work and look intentional.
- [x] `dotnet test` is green. No new package.

## Notes

FR-1, FR-5 (styles only), FR-9. Copy values from `overhaul.css`; drop the `.ov` scope. Use Pico variables where Pico reads them, and element rules only where it doesn't. Check PO list, PO detail, product edit and the stock count page in the browser.
