# WI-09-5 — Button roles, overflow menu, confirm dialog

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-4
- **Estimate:** M

## Goal

Each page has one primary action, secondary actions are outlined, and destructive actions sit in a menu or in a small danger button and confirm in the app's own dialog.

## Scope

- In: the detail and list pages that carry actions (PO detail and receive, product detail, supplier detail and links, stock reverse and verify, users), `_Layout` (`dialog#confirm`), `site.js` (`htmx:confirm`, menu close), `site.css`, tests.
- Out: new operations. Server rules. `#modal` behaviour.

## Tasks

- [x] Roles per FR-5. PO detail header: Print and Edit secondary, Submit / Receive goods primary, then a `<details class="menu">` with Close short, Cancel order and Delete draft (each still its own `<form method="post">` with `hx-post` and `hx-confirm`).
- [x] Row actions that are destructive (Archive, Reverse, Remove link) become small text buttons in the danger colour; Restore and Edit stay secondary.
- [x] `.menu` / `.menu-pop` / `.menu-item` styles as in `/Dev/Overhaul`; the menu closes on outside click and Escape; `summary` has an accessible name.
- [x] `_Layout` adds `<dialog id="confirm">` with title, message and Cancel / Confirm buttons. `site.js` handles `htmx:confirm`: if the event has a question, `preventDefault`, show the dialog with the text, focus Cancel for danger sources and Confirm otherwise; Confirm calls `evt.detail.issueRequest(true)`; Cancel and Escape do nothing. The Confirm label is the source button's text.
- [x] Verify-stock fix is a danger action. Restore stays secondary.
- [x] Print CSS hides `#confirm` and menus.
- [x] Tests: PO detail HTML for a submitted order contains `class="menu"` with the Cancel order form inside it and Receive goods outside it; Draft PO shows Submit order as the primary and Delete draft inside the menu; existing no-JavaScript post tests (submit, cancel, delete, reverse) still pass unchanged; served `site.js` contains `htmx:confirm`; `#modal` and `#confirm` are different elements.

## Acceptance criteria

- [x] With JavaScript, Cancel order opens `#confirm` with the existing sentence and focus on Cancel. Cancelling leaves the order alone. Confirming cancels it as today.
- [x] Archive on a product and Reverse on a receipt open the same dialog with their current sentences.
- [x] With JavaScript off, the same forms still post (no dialog).
- [x] `dotnet test` is green.

## Notes

FR-5, FR-6. htmx 2 fires `htmx:confirm`; the request proceeds unless the listener calls `preventDefault` and later `issueRequest`. Two dialogs must not share one element.

### As built

- The menu holds Close short (offered once something is received), Cancel order and Delete draft, with a rule before Delete draft. It is only rendered when at least one of them applies, and never for a Viewer.
- PO header order: Print, Edit (secondary), then Receive goods or Submit order (primary), then the menu.
- Product and supplier Edit are now secondary (the move buttons and the page have no solid primary). Archive, Reverse (PO receipts and stock movements), Remove (PO line, supplier link) and category Archive are `text danger`. Restore stays `secondary outline`. The verify-stock Fix is a solid `danger` button.
- The overflow `summary` is a plain disclosure (no `role=button`), so assistive tech still hears expanded / collapsed.
- Danger sources (inside `.menu` or `.danger`) focus Cancel and get a red Confirm; Submit order and Restore focus Confirm.
- `#confirm` is only rendered for signed-in pages; where it is missing htmx falls back to the browser prompt.
- Checked in the browser: menu opens, closes on outside click and Escape; Cancel and Escape leave the order alone; Confirm closes the order short.
