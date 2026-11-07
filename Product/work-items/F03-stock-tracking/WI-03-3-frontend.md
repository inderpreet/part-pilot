# WI-03-3 — Movements, history, stock count

- **Feature:** [F03 — Stock tracking](../../Features/F03-stock-tracking.md)
- **Status:** Planned
- **Depends on:** WI-03-2, WI-02-3
- **Estimate:** L

## Goal

Staff can record movements quickly (single, bulk, count) and everyone can see history.

## Scope

- In: quick move modal, bulk page, count page, movements list, product history tab, reverse action, admin verify page.
- Out: PO receive screen (WI-05-3).

## Tasks

- [ ] Quick move modal (`/Stock/Move`): type segmented control (Receive/Issue/Adjust/Return), qty, reason (required for Adjust), reference, note, date.
      Buttons "+ In" / "− Out" / "Adjust" on product list rows and detail page. On success: close modal, refresh on-hand + history, flash.
- [ ] `/Stock/Bulk`: type select, rows added via `_ProductPicker` + qty (HTMX add/remove row), line-level errors, post all.
- [ ] `/Stock/Count`: filters (category, bin, search), table with current qty and counted input; "Preview" shows differences; "Post" creates Count transactions.
      Optional "blind count" toggle hides current qty.
- [ ] Product detail History tab (`?handler=History`): paged table; Reversal rows link to original; "Reverse" button with confirm for CanEdit.
- [ ] `/Stock/Movements`: filters (date range, type, product, user), paged table, links to product/PO.
- [ ] `/Admin/VerifyStock`: run check, show mismatches, "Fix" button.
- [ ] Page tests: quick move success + negative-stock error rendering, Viewer sees no movement buttons.

## Acceptance criteria

- [ ] Issue more than on hand (setting off) shows inline error, nothing posted.
- [ ] Count of 50 products with 3 differences posts exactly 3 transactions.
- [ ] Reversed transaction shows "Reversed" badge and cannot be reversed again.

## Notes

Optimise for keyboard: Enter submits, scanner input in picker selects exact match.
