# WI-10-4 — Order shortages, build, builds history

- **Feature:** [F10 — Projects & BOMs](../../Features/F10-project-boms.md)
- **Status:** Done
- **Depends on:** WI-10-3
- **Estimate:** M

## Goal

From a project page, a user can send short parts to a purchase order they choose, and can record a build that issues the parts from stock.

## Scope

- In: `/Projects/{id}/Order` review and post, the Build modal and handler, the Builds card, action buttons in the project page header, tests, F10 docs and statuses.
- Out: reserving stock, reversing a whole build, BOM import / export.

## Tasks

- [x] "Order shortages" in the project page header (`CanEdit`, shown only when at least one line has `to order > 0`; secondary button). Links to `/Projects/{id}/Order`.
- [x] `/Projects/{id}/Order` (`CanEdit`), GET: a card listing every line with `to order > 0`: part, short, **in stock / on order / in PO draft** (draft quantity with links to every draft PO that holds the part, whoever made it) and, per line, an **Action** select and a **quantity**:
  - Part in no draft: Add (default) or Skip; quantity defaults to `to order`, raised to the supplier link's minimum order quantity when the chosen supplier has one.
  - Part already in a draft: Skip, Update quantity or Add. Default Skip when drafts cover `to order`, Update quantity when they cover only part of it. Update quantity shows a draft select when several drafts hold the part (the chosen target first, if it holds the part) and the quantity field is the **new total on that draft line**, prefilled with its current quantity plus the uncovered shortfall (rounded to the minimum order quantity). Add is not offered when the chosen target already holds the part. The row is labelled "Already in draft PO-…" with a link.
  - Archived parts are shown disabled with the reason.
  - The action select works without JavaScript; htmx only hides fields that do not apply (draft select, quantity for Skip).
- [x] Target selector (a `.form-row` section), needed only for lines set to Add and hidden when none is: radio "Existing draft purchase order" with a select of Draft POs (number, supplier, line count; empty state says there are none), or "New draft for supplier" with a select of active suppliers. Changing the supplier re-prices on the server, not in the browser. No draft is created when no line is set to Add.
- [x] POST: calls `ProjectService.AddShortagesToOrderAsync` with one `ShortageDecision` per line. Validation errors re-render the form with field errors (HTTP 422 for htmx). On success, redirect to `/Projects/{id}/Order?handler=Result` (see As built) with a flash like "Added 3 lines, updated 1, skipped 1" and a result card listing every added, updated (old to new quantity, with PO link) and skipped line with the reason (archived part, zero quantity, already on this PO). Nothing is submitted automatically.
- [x] "Build" action in the project page header (`CanEdit`): the primary button when the project state is Ready, otherwise inside the overflow menu. GET `?handler=Build` opens a modal: Units (default = remaining, minimum 1), Note, and the list of what will be issued. POST calls `ProjectService.BuildAsync`; on a short part it re-renders the modal with one error per part and posts nothing; on success it flashes "Built N units. Issued X parts." and the page refreshes (state chip, remaining, stock columns).
- [x] Builds card on the project page: date, units, who, note, and a link to the stock history filtered by the build reference. Newest first.
- [x] Project list and page state chips show **Built** once remaining units reach 0.
- [x] Every action is a plain form that also works without JavaScript; the confirm text states the effect ("Issue 40 parts from stock for 5 units?").
- [x] Authorization: `CanEdit` on the order page and on Build; a Viewer sees neither button and gets 403 on direct posts.
- [x] Tests:
  - [x] Add lands on the chosen existing draft (lines, quantities, prices) and the flash and result card are shown; Update quantity changes the existing draft line and keeps its price; Skip changes nothing; mixed actions in one post; a part held by two drafts can be updated on the one picked.
  - [x] "New draft for supplier" creates a numbered draft for that supplier containing the lines set to Add; lines set to Skip are left out; a post with only Update / Skip creates no draft; a non-draft PO cannot be targeted.
  - [x] Order button hidden when nothing is short; archived part shown disabled.
  - [x] A draft that was submitted between opening the page and posting: the post is refused, no PO is changed, and the page re-renders with the current drafts and a message.
  - [x] Update quantity is never offered for a part that is only on a Submitted or later PO (those count as "on order", not "in draft").
  - [x] A part already in a draft (made by anyone, for any supplier) shows in stock / on order / in PO draft with links; the default action is Skip when drafts cover `to order` and Update quantity when they cover part; the prefilled update quantity is current plus the uncovered shortfall; Add is not offered when the chosen target already holds the part; the same page works as a plain form post.
  - [x] Build with enough stock: issue rows appear in the stock history with the reference, on-hand drops, the Builds card and the chip update, remaining units fall; build with a short part posts nothing and lists the part.
  - [x] Build works as a plain form post; role matrix (Viewer 403, anonymous redirected).
  - [x] The product page for a built part shows the Issue row in its history.
- [x] Docs: set statuses, tick the checklist and add "As built" notes to WI-10-1..4, update `Product/README.md` (F10 row and work-item rows, Done), `Product/Features/F10-project-boms.md` (status, work-item table, resolve or carry open questions), `Product/architecture.md` (data model, invariant, D18). Run `check_scaffold.py` (0 errors).

## Acceptance criteria

- [x] Each short line can be added, skipped or (when already in a draft) have its draft quantity updated, with a clear added / updated / skipped result.
- [x] A build issues every part in one step, or none of them with a clear list of what is short.
- [x] A Ready project shows Build as its primary action; a Viewer sees neither Order nor Build.
- [ ] Narrow screens (390px, 768px) keep the review table scrolling inside its card, with the actions reachable.
- [x] `dotnet build` has zero warnings, `dotnet test` is green, F10 is marked Done in the README.

## Notes

- Reuse the F09 patterns: `.form-row` sections, `.action-bar`, the app confirm dialog, `#modal` for the Build form, a neutral `.card` for the skipped list (`.alert` is the red error style, so do not use it for this).
- The review page is a normal page, not a modal, because it has a table and two selects. Build is a modal because it is one number and a confirmation.
- Draft PO quantities are information, not supply (F10 FR-4): they never change `to order`, only the default actions and suggested quantities on this page.
- Issued purchase orders are never changed by this page: Add and Update quantity work on Draft POs only (F05 allows line edits on drafts only). If a draft was submitted after the page was opened, the post is refused with nothing applied and the review is shown again with the current drafts.

## As built

- Pages: `Pages/Projects/Order.cshtml(.cs)` with partials `_OrderForm` (the "Parts to order" table and the "Where to order" target) and `_OrderLines` (the table, also served alone by `?handler=Lines` when the target changes, so the suggested quantities follow the supplier's minimum order quantity on the server). Build lives on the project page: handlers `Build` (GET dialog, POST) and `BuildPreview` (GET, the "Issue N parts from stock for U units?" list that refreshes as the number is typed) with partials `_BuildModal`, `_BuildForm`, `_BuildList`. `_ProjectActions` holds the Order shortages and primary Build buttons and rides along as an out-of-band swap after a BOM change, so they appear and disappear without a reload.
- **Deviation from the task text:** a good order post redirects to `Order?handler=Result`, a result card (added / updated with old and new quantity / skipped with the reason, each with its PO link, plus "Open purchase order") instead of straight to the PO. The result is carried in TempData and shown once; a refresh returns to the project. Nothing is submitted. Rationale: the PO page cannot show the skipped lines, and several POs can be touched in one post.
- Defaults follow the drafts (Add when no draft holds the part, Skip when drafts cover it, Update when they cover part, quantity = current + uncovered, rounded up to the minimum order quantity of the supplier the part would end up with). Add is not offered when the chosen draft already holds the part; Update only when a draft holds it. Parts only on submitted orders are "on order" and offer Add only.
- What does not apply is hidden with CSS `:has()` (no JavaScript needed): quantity for Skip, the draft picker unless Update, the whole target section when nothing is set to Add, and the existing-draft / new-draft halves of the target by the chosen radio. Archived parts show "Not offered" and are posted as Skip so row numbers stay contiguous for model binding.
- A draft submitted after the page was opened: the service refuses the post, nothing is applied, and the page re-renders with the current drafts and the message "A purchase order changed while this page was open".
- Build is always in the overflow menu and also the primary button when the project is Ready. The "confirmation" is the visible effect sentence in the dialog (no separate browser confirm). On a short or archived part nothing is posted and the dialog lists the parts. The success flash reads "Built N units. Issued X parts from stock." The Builds card links each build to `/Stock/Movements?search=<project name> build #<id>`.
- Tests: `ProjectPagesTests.OrderBuild.cs` (17 tests) covering the list above, plus the Order/Build role matrix.
- Not done: the 390px / 768px visual check (the preview server could not be started in this session). Checked in the code only: the review table and BOM table sit in `.table-wrap` cards that scroll inside, and the dialog reuses the existing modal. Left unticked on purpose; do it once in a browser.
