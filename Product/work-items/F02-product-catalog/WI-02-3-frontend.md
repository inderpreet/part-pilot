# WI-02-3 — Product list/detail/edit, categories

- **Feature:** [F02 — Product catalog](../../Features/F02-product-catalog.md)
- **Status:** Done
- **Depends on:** WI-02-2, WI-00-3
- **Estimate:** M

## Goal

Users can browse, search, create, edit and archive products and categories.

## Scope

- In: `/Products`, `/Products/{id}`, `/Products/Edit`, `/Categories`, product picker partial.
- Out: stock actions on detail page (WI-03-3), supplier tab (WI-04-3).

## Tasks

- [x] `/Products`: search box (live, 300 ms debounce, autofocus for scanner input), category + status filters, sortable table,
      pager, low-stock badge; Enter on exact SKU/barcode match navigates to detail.
- [x] `_ProductRows` partial for HTMX swaps; `hx-push-url` keeps filters in URL.
- [x] `/Products/{id}`: header (SKU, name, status), fields card, on-hand big number, tab area placeholders (History, Suppliers, POs, Audit).
- [x] `/Products/Edit/{id?}`: form with validation, category select, money input, SKU read-only when locked; concurrency error message with reload link.
- [x] Archive/restore button with `hx-confirm`.
- [x] `_ProductPicker` partial: input + dropdown results (`/Products/Lookup`), sets hidden ProductId — reused by F03/F05.
- [x] `/Categories`: list with inline create/edit (HTMX row swap), archive toggle.
- [x] Hide edit actions for Viewer.
- [x] Page tests: list renders, HTMX partial returned on `HX-Request`, Viewer POST returns 403.

## Acceptance criteria

- [x] Create → appears in list → edit → archive → hidden under "Active" filter, visible under "Archived".
- [x] Scanner-style input (type SKU + Enter) opens product detail.
- [x] Works at tablet width.

## Implementation notes

- **Categories are one page.** Create, edit and archive happen inline on `/Categories` through htmx handlers (`?handler=New|Edit|Row|Save|SetActive`), so there is no separate `/Categories/Edit/{id?}` route (the feature spec table is updated). The page is `CanView`; every handler that changes data re-checks `CanEdit` with `this.CanEditAsync()` and returns `Forbid()`, because the page-level attribute cannot vary per handler. Creating returns the whole section (so the new row lands in sorted position) via `HX-Retarget`/`HX-Reswap`; editing and archiving swap just the row; failures come back as 422 with the form and its errors.
- **Scanner Enter.** The search form is a plain GET form (no `hx-*` on the form itself) with a visually hidden default submit button `go=true`. Enter in the search box therefore submits normally: an exact SKU (case-insensitive) or barcode match redirects to the detail page; otherwise the list shows a "no exact match" notice plus partial matches. Typing still updates `#results` live. `go` is `true`, not `1`, because ASP.NET's bool binder does not accept `1`.
- **Product picker** (`_ProductPicker` + `/Products/Lookup` + `site.js`): type or scan, click a row (or Arrow keys + Enter/click) to fill a hidden input named by `FieldName` (e.g. `Input.ProductId`). Typing again clears the hidden id, so a stale selection is never submitted. Enter in the picker forces an immediate lookup and picks the match when there is exactly one, or when the first is an exact SKU/barcode hit (scanner flow). Demo on `/Dev/Components`. There are no JS unit tests; behaviour was checked in a browser and the server side (markup, `data-exact`, archived filtering) is covered by tests.
- **Edit form.** Unit cost is typed text parsed by `MoneyParser` (blank = 0.00; errors attach to `Input.UnitCost`). Reorder point/quantity blank = 0. The hidden `Input.RowVersion` carries the load-time version; a stale save shows the service's conflict message plus a "Reload the latest version" link. The category select lists active categories plus the product's current one (labelled archived). `SkuLocked` makes the SKU input `readonly` (still posted); the lock itself is still stubbed until WI-03-2.
- **Archive/restore** posts to `/Products/{id}?handler=Archive` with the row version. Success and failure both end on the detail page with a flash (TempData, since a flash cannot ride an `HX-Redirect`); `Flash`/`FlashError` helpers and a red error variant of `_Flash` were added to `PageExtensions`.
- **Navigation:** Products is now an available nav item (`CanView`); the nav test was updated.
- **Styling:** Pico makes buttons inside a `<form>` full width and redefines `--pico-color` to white on buttons, so toolbar and picker rules in `site.css` are scoped to beat it.
- **No new NuGet packages or JS libraries.**
