# WI-03-3 — Movements, history, stock count

- **Feature:** [F03 — Stock tracking](../../Features/F03-stock-tracking.md)
- **Status:** Done
- **Depends on:** WI-03-2, WI-02-3
- **Estimate:** L

## Goal

Staff can record movements quickly (single, bulk, count) and everyone can see history.

## Scope

- In: quick move modal, bulk page, count page, movements list, product history tab, reverse action, admin verify page.
- Out: PO receive screen (WI-05-3).

## Tasks

- [x] Quick move modal (`/Stock/Move`): type segmented control (Receive/Issue/Adjust/Return), qty, reason (required for Adjust), reference, note, date.
      Buttons "+ In" / "− Out" / "Adjust" on product list rows and detail page. On success: close modal, refresh on-hand + history, flash.
- [x] `/Stock/Bulk`: type select, rows added via `_ProductPicker` + qty (HTMX add/remove row), line-level errors, post all.
- [x] `/Stock/Count`: filters (category, bin, search), table with current qty and counted input; "Preview" shows differences; "Post" creates Count transactions.
      Optional "blind count" toggle hides current qty.
- [x] Product detail History tab (`?handler=History`): paged table; Reversal rows link to original; "Reverse" button with confirm for CanEdit.
- [x] `/Stock/Movements`: filters (date range, type, product, user), paged table, links to product/PO.
- [x] `/Admin/VerifyStock`: run check, show mismatches, "Fix" button.
- [x] Page tests: quick move success + negative-stock error rendering, Viewer sees no movement buttons.

## Acceptance criteria

- [x] Issue more than on hand (setting off) shows inline error, nothing posted.
- [x] Count of 50 products with 3 differences posts exactly 3 transactions.
- [x] Reversed transaction shows "Reversed" badge and cannot be reversed again.

## Notes

Optimise for keyboard: Enter submits, scanner input in picker selects exact match.

## Implementation notes

- **Pages:** `Pages/Stock/{Index,Move,Bulk,Count,Movements,Reverse}` and `Pages/Admin/VerifyStock`; shared partials `_MovementTable`, `_MoveButtons`, `_Pager` (now fragment-aware via a `Handler`). `/Stock` (the nav item) redirects to the movements list.
- **Quick move** is a real page (`/Stock/Move?productId=&type=`) that htmx loads into the dialog; without JavaScript the "+ In / − Out / Adjust" links open the full page. Offers Receive/Issue/Adjust/Return; `Count` and `Reversal` are rejected by the page (counts have their own page, reversals their own button). Success flashes through TempData and answers `HX-Refresh` (dialog) or redirects to the product (full page); a rejected post is a 422 that swaps the form and keeps what was typed. After a product is picked, focus moves to Quantity (`data-focus-after-pick`) for the scanner flow.
- **Bulk:** add/remove line are htmx round-trips of the whole form so the line indexes stay contiguous and the service's `Lines[n].Field` errors line up with the inputs. A half-filled line is an error, a completely blank one is dropped. Capped at 200 lines.
- **Count:** the sheet is one form; filters travel as hidden fields and counts as `Counted[productId]` (a blank box = not counted, which differs from 0). Preview and Post both go through `StockService.PreviewCountAsync` / `PostAsync`, so only differing rows produce a `Count` transaction. Capped at 200 rows per sheet; "Blind count" hides the system quantity. This added a **bin filter and bin sort** to `ProductService.SearchAsync` (`ProductListQuery.Bin`, `Sort=bin`; unbinned products sort last).
- **Reverse:** plain POST form (redirects back to the page's own `returnUrl`, validated with `SafeReturnUrl`) upgraded with `hx-post` + `hx-confirm`; the result message rides TempData so htmx answers `HX-Refresh`. Reversed rows get a "Reversed" badge and no button; reversal rows link to the original.
- **History:** product detail has the live History tab (`?handler=History` returns the table fragment; its pager fetches the fragment without touching the address bar) plus a link to the filtered global list (`/Stock/Movements?productId=`).
- **Movements filters:** date range, type, product, user, text. Dates are whole *browser-local* days: `site.js` fills a hidden `tz` field with `getTimezoneOffset()` and `LocalTimeInput` converts to UTC instants (server zone as the no-JS fallback). The same field converts the "Date and time" typed into the move forms.
- **Verify stock** (`/Admin/VerifyStock`, linked from the admin page): "Run check" shows mismatches, "Fix" rewrites the cache from the ledger (post-redirect-get with a flash).
- **Deviations / follow-ups:** PO references are shown as plain text ("PO line #n") until F05 adds PO pages to link to. Reasons are free text with a suggestion list (resolved the F03 open question).
- **Tests:** `Pages/StockPagesTests` (dialog GET/POST, inline 422, role checks, bulk atomicity and line errors, 50-product count with 3 differences, reverse rules, filters, history paging, verify check/fix) and `Services/StockSupportTests` (time conversion, error mapping, bin filter/sort, user list). The test auth handler now resolves the seeded user's real id (stock rows have a foreign key to the acting user).