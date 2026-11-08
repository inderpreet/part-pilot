# WI-10-4 — Order shortages, build, builds history

- **Feature:** [F10 — Projects & BOMs](../../Features/F10-project-boms.md)
- **Status:** Planned
- **Depends on:** WI-10-3
- **Estimate:** M

## Goal

From a project page, a user can send short parts to a purchase order they choose, and can record a build that issues the parts from stock.

## Scope

- In: `/Projects/{id}/Order` review and post, the Build modal and handler, the Builds card, action buttons in the project page header, tests, F10 docs and statuses.
- Out: reserving stock, reversing a whole build, BOM import / export.

## Tasks

- [ ] "Order shortages" in the project page header (`CanEdit`, shown only when at least one line has `to order > 0`; secondary button). Links to `/Projects/{id}/Order`.
- [ ] `/Projects/{id}/Order` (`CanEdit`), GET: a card listing every line with `to order > 0`: part, short, **in stock / on order / in PO draft** (draft quantity with links to every draft PO that holds the part, whoever made it) and, per line, an **Action** select and a **quantity**:
  - Part in no draft: Add (default) or Skip; quantity defaults to `to order`, raised to the supplier link's minimum order quantity when the chosen supplier has one.
  - Part already in a draft: Skip, Update quantity or Add. Default Skip when drafts cover `to order`, Update quantity when they cover only part of it. Update quantity shows a draft select when several drafts hold the part (the chosen target first, if it holds the part) and the quantity field is the **new total on that draft line**, prefilled with its current quantity plus the uncovered shortfall (rounded to the minimum order quantity). Add is not offered when the chosen target already holds the part. The row is labelled "Already in draft PO-…" with a link.
  - Archived parts are shown disabled with the reason.
  - The action select works without JavaScript; htmx only hides fields that do not apply (draft select, quantity for Skip).
- [ ] Target selector (a `.form-row` section), needed only for lines set to Add and hidden when none is: radio "Existing draft purchase order" with a select of Draft POs (number, supplier, line count; empty state says there are none), or "New draft for supplier" with a select of active suppliers. Changing the supplier re-prices on the server, not in the browser. No draft is created when no line is set to Add.
- [ ] POST: calls `ProjectService.AddShortagesToOrderAsync` with one `ShortageDecision` per line. Validation errors re-render the form with field errors (HTTP 422 for htmx). On success, redirect to the target PO (or, when only quantities were updated, to the first updated PO) with a flash like "Added 3 lines, updated 1, skipped 1" and a result card listing every added, updated (old to new quantity, with PO link) and skipped line with the reason (archived part, zero quantity, already on this PO). Nothing is submitted automatically.
- [ ] "Build" action in the project page header (`CanEdit`): the primary button when the project state is Ready, otherwise inside the overflow menu. GET `?handler=Build` opens a modal: Units (default = remaining, minimum 1), Note, and the list of what will be issued. POST calls `ProjectService.BuildAsync`; on a short part it re-renders the modal with one error per part and posts nothing; on success it flashes "Built N units. Issued X parts." and the page refreshes (state chip, remaining, stock columns).
- [ ] Builds card on the project page: date, units, who, note, and a link to the stock history filtered by the build reference. Newest first.
- [ ] Project list and page state chips show **Built** once remaining units reach 0.
- [ ] Every action is a plain form that also works without JavaScript; the confirm text states the effect ("Issue 40 parts from stock for 5 units?").
- [ ] Authorization: `CanEdit` on the order page and on Build; a Viewer sees neither button and gets 403 on direct posts.
- [ ] Tests:
  - [ ] Add lands on the chosen existing draft (lines, quantities, prices) and the flash and result card are shown; Update quantity changes the existing draft line and keeps its price; Skip changes nothing; mixed actions in one post; a part held by two drafts can be updated on the one picked.
  - [ ] "New draft for supplier" creates a numbered draft for that supplier containing the lines set to Add; lines set to Skip are left out; a post with only Update / Skip creates no draft; a non-draft PO cannot be targeted.
  - [ ] Order button hidden when nothing is short; archived part shown disabled.
  - [ ] A draft that was submitted between opening the page and posting: the post is refused, no PO is changed, and the page re-renders with the current drafts and a message.
  - [ ] Update quantity is never offered for a part that is only on a Submitted or later PO (those count as "on order", not "in draft").
  - [ ] A part already in a draft (made by anyone, for any supplier) shows in stock / on order / in PO draft with links; the default action is Skip when drafts cover `to order` and Update quantity when they cover part; the prefilled update quantity is current plus the uncovered shortfall; Add is not offered when the chosen target already holds the part; the same page works as a plain form post.
  - [ ] Build with enough stock: issue rows appear in the stock history with the reference, on-hand drops, the Builds card and the chip update, remaining units fall; build with a short part posts nothing and lists the part.
  - [ ] Build works as a plain form post; role matrix (Viewer 403, anonymous redirected).
  - [ ] The product page for a built part shows the Issue row in its history.
- [ ] Docs: set statuses, tick the checklist and add "As built" notes to WI-10-1..4, update `Product/README.md` (F10 row and work-item rows, Done), `Product/Features/F10-project-boms.md` (status, work-item table, resolve or carry open questions), `Product/architecture.md` (data model, invariant, D18). Run `check_scaffold.py` (0 errors).

## Acceptance criteria

- [ ] Each short line can be added, skipped or (when already in a draft) have its draft quantity updated, with a clear added / updated / skipped result.
- [ ] A build issues every part in one step, or none of them with a clear list of what is short.
- [ ] A Ready project shows Build as its primary action; a Viewer sees neither Order nor Build.
- [ ] Narrow screens (390px, 768px) keep the review table scrolling inside its card, with the actions reachable.
- [ ] `dotnet build` has zero warnings, `dotnet test` is green, F10 is marked Done in the README.

## Notes

- Reuse the F09 patterns: `.form-row` sections, `.action-bar`, the app confirm dialog, `#modal` for the Build form, a neutral `.card` for the skipped list (`.alert` is the red error style, so do not use it for this).
- The review page is a normal page, not a modal, because it has a table and two selects. Build is a modal because it is one number and a confirmation.
- Draft PO quantities are information, not supply (F10 FR-4): they never change `to order`, only the default actions and suggested quantities on this page.
- Issued purchase orders are never changed by this page: Add and Update quantity work on Draft POs only (F05 allows line edits on drafts only). If a draft was submitted after the page was opened, the post is refused with nothing applied and the review is shown again with the current drafts.
