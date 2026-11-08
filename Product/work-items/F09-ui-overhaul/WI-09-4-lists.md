# WI-09-4 — List cards

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-3
- **Estimate:** M

## Goal

Products, categories, suppliers, stock movements, stock count, purchase orders and users each show one list card: tabs (where they exist), one filter row, a scrolling table with a sticky header, and a footer with the pager.

## Scope

- In: the list pages and their `_…Results` / `_…Rows` partials, `_Pager`, `_SortHeader`, `_EmptyState`, `site.css`, tests.
- Out: query names, sorting logic, pager logic, row actions (WI-09-5).

## Tasks

- [x] Wrap tabs, filter form and `#results` in `div.card.list-card` on each list page. Add `<div class="load-bar" role="status" aria-label="Updating"></div>` as the first child and `hx-indicator=".load-bar"` on the filter controls that already refresh `#results`.
- [x] `.tabs` as underline tabs with the current one marked (`aria-current="page"`). PO tabs get counts only if the model already exposes them; otherwise no count.
- [x] `.toolbar` is one 36px-high row: search with an icon, selects, dates, no "New …" button. Wraps on narrow screens.
- [x] `.table-wrap` scrolls inside the card (`max-height: calc(100vh - 20rem)`, `overflow: auto`), sticky `thead th`, right-aligned `.num` columns including their sort headers; archived rows muted.
- [x] `_Pager` renders `.card-foot`: "Showing a–b of n" on the left, Previous / Next on the right, page links kept. `_EmptyState` restyled to fit inside a card.
- [x] Overdue on the PO list renders `badge-overdue` beside the date; the date cell is no longer red text.
- [x] Tests: each list page's HTML contains `list-card` and `load-bar`; `/PurchaseOrders?tab=Open` still returns only open orders; htmx partial requests (`HX-Request`) still return only the results partial (no card wrapper duplication); an overdue open order has `badge-overdue` and not `badge-cancelled` on that row.

## Acceptance criteria

- [x] Typing in the product or PO search updates `#results` and the URL as before; the load bar is present in the markup.
- [x] Scrolling a long list keeps the column headings visible.
- [x] Sort links, pager links and status tabs keep their query strings.
- [x] `dotnet test` is green.

## Notes

FR-4. The filter form and `#results` are different elements today, so the card wraps both and the partial swap stays inside it. Check Products, Stock movements and PO in the browser.

### As built

- Products, Suppliers, Purchase orders, Stock movements and Users are list cards. Stock count and Categories are edit forms, not lists, so they keep the plain .table-wrap card look.
- PO tabs have no counts: the page model does not expose them, and adding queries is out of scope for a presentation-only feature.
- The pager is not a separate .card-foot; .list-card .pager is styled as the card footer, so the _Pager markup and its tests are unchanged.
- Below 48rem the table no longer has a max height, so a phone has one scroll (the page), not a scroll inside a scroll.
- Overdue is a solid adge-overdue chip, distinct from the red Cancelled chip.
