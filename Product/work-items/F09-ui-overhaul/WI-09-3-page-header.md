# WI-09-3 — Page header

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-2
- **Estimate:** M

## Goal

The title, its status chips and its actions share one header row on every signed-in page. The breadcrumb in the top bar names the page.

## Scope

- In: `_Layout.cshtml` (header from `ViewData` and sections), the pages that today open with a `.toolbar` holding a back link, badges or a "New …" button, `site.css`, tests.
- Out: moving the filter rows (WI-09-4), the overflow menu (WI-09-5), forms (WI-09-6).

## Tasks

- [x] `_Layout` renders `div.page-head` with `<h1>` from `ViewData["Title"]` and optional sections `Leading`, `Badges`, `Subtitle`, `Actions`. A page that defines none still shows the title. `ViewData["Header"] = "plain"` renders `<h1>` only.
- [x] Anonymous pages set `Header = "plain"`: Login, Setup, ChangePassword (signed in, but a narrow card), AccessDenied, Error. `_PrintLayout` is unchanged.
- [x] Top bar breadcrumb: section label, then `ViewData["Crumb"]` when the page sets one (detail pages set the record name).
- [x] Move into the header: list pages' "New …" button (`Actions`); detail pages' back link (`Leading`), status chips (`Badges`) and action buttons (`Actions`); the product, supplier and PO detail subtitle.
- [x] Pages with a `.toolbar` that only held those items lose it. Pages whose toolbar also holds filters keep the filter part (WI-09-4 restyles it).
- [x] Tests: a detail page puts the status chip inside `class="page-head"`; a list page puts "New …" inside `page-head` for Staff and omits it for a Viewer; Login has no `class="page-head"` but still has its `<h1>`.

## Acceptance criteria

- [x] `/PurchaseOrders` shows the title with "New purchase order" on the right.
- [x] `/PurchaseOrders/{id}` shows back link, number, status chips and the action buttons in the header.
- [x] Role rules unchanged: a Viewer sees no New/Edit/Receive controls.
- [x] `dotnet test` is green.

## Notes

FR-3. The overflow menu comes in WI-09-5; until then danger actions stay as buttons in `Actions`.
