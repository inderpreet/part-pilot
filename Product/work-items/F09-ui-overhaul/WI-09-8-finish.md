# WI-09-8 — Narrow screens, print, gallery, clean-up

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-7
- **Estimate:** M

## Goal

The overhaul holds up on a tablet and a phone, prints cleanly, and has one stylesheet.

## Scope

- In: `site.css` media rules, print rules, `/Dev/Components` gallery, `/Dev/Overhaul` and `overhaul.css`, docs.
- Out: new patterns.

## Tasks

- [x] Narrow rules from FR-10: drawer below 64rem, side columns under the main card below 80rem, stacked header actions and full-width filters below 40rem, tables scroll in their card, first column sticky in `.table-wrap`, `(pointer: coarse)` minimum 2.75rem for buttons, menu items and rail links.
- [x] Print: hide the rail, top bar, filters, buttons, menus, dialogs and toasts on every page except the PO print sheet; the print sheet is unchanged and still fits A4 and Letter.
- [x] `/Dev/Components` shows each shared pattern (chips, buttons, menu, alert, field error, card, stat, timeline, tabs) (it no longer links to `/Dev/Overhaul`, which was retired; see As built).
- [x] One stylesheet: `overhaul.css` is deleted. Changed from the plan: the `/Dev/Overhaul` page was deleted with it rather than repointed at `site.css` (see As built).
- [x] Update `Product/architecture.md` D17 and the README row to describe the shipped pattern set.
- [x] Tests: `/css/site.css` contains the print and `max-width: 64rem` rules; the PO print page still has no `class="rail"`; `overhaul.css` is no longer referenced.

## Acceptance criteria

- [x] At 390px and 768px the main screens are usable: no horizontal page scroll, drawer works, actions reachable.
- [x] Printing a list page shows only the table; printing a PO shows the print sheet.
- [x] One stylesheet (`site.css`) carries the product's look.
- [x] `dotnet test` is green, `check_scaffold.py` reports 0 errors, and F09 is marked Done in the README.

## Notes

FR-10, FR-11, FR-12. This item is the sweep: run the main screens at 1440, 768 and 390.

### As built

- **`/Dev/Overhaul` was retired instead of repointed.** It was a 650-line static prototype with its own `--ov-*` tokens and `.btn` classes, so pointing it at `site.css` would have meant rewriting every sample on the real classes, and the live screens are now the picture. `Overhaul.cshtml`, its model, `_OverhaulLayout.cshtml` and `overhaul.css` are deleted; `site.css` is the only app stylesheet besides vendored Pico. FR-12 and the feature table say so.
- `/Dev/Components` is the gallery: it adds button roles, the overflow menu with the confirm dialog, chips (including overdue), card / stat / key-value, timeline, tabs and section nav, alert and field error, all on the real classes.
- Narrow rules: drawer below `64rem` (built in WI-09-2); side columns drop below `80rem` (was `75rem`); below `40rem` header actions fill the row and every list filter goes full width; below `48rem` the first column of a scrolling table is sticky; at `(pointer: coarse)` buttons, menu items, the menu toggle, tabs, the section nav and rail links are at least `2.75rem`.
- Print: besides the rail, top bar, buttons, menus, dialogs and toasts, a printed page also hides the filter row, tabs, section nav, back link, page actions, the load bar, the sticky save bar and the row move buttons. Tables print in full (no inner scroll, no sticky header), cards lose their shadow, and the page background is white. The PO print sheet is a separate layout and is unchanged.
- Checked at 390px, 768px and a wide window: no horizontal page scroll (wide tables scroll inside their card), the drawer opens, header actions and filters are reachable, and a printed list shows only its title and table.
- Tests: the gallery shows each pattern; the prototype page and stylesheet are gone and nothing references them; `site.css` carries the `64rem`, `80rem`, `40rem`, coarse-pointer and print rules; the PO print sheet has no `class="rail"`, top bar or confirm dialog.
