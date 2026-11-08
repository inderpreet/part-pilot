# WI-09-7 — Detail pages

- **Feature:** [F09 — UI overhaul](../../Features/F09-ui-overhaul.md)
- **Status:** Done
- **Depends on:** WI-09-6
- **Estimate:** M

## Goal

Product, supplier and purchase order detail pages are built from cards, chips and the shared components.

## Scope

- In: `Products/Detail`, `Suppliers/Detail`, `PurchaseOrders/Detail`, their partials, `site.js` (section-nav hash), `site.css`, tests.
- Out: new data on these pages. The Audit tab content (F08).

## Tasks

- [x] PO detail: Lines in a card (a received-progress meter per line is optional if the model already has both quantities); Supplier, Summary (total, received value, outstanding) and Order details in cards beside or below; Receipts card below. Overdue renders `badge-overdue` beside the status chip.
- [x] Product detail: on-hand number in `--it-display`, coloured when low or zero, in a card with its chip; the tab row becomes `.section-nav` (History, Suppliers, Purchase orders as `href="#…"`; Audit an inert `span` with "Soon"). `site.js` sets `aria-current` for the current hash and on History when the hash is empty.
- [x] Supplier detail: details card and linked-products card.
- [x] Use the page header sections (WI-09-3) for back link, chips and actions.
- [x] Tests: PO detail HTML for an overdue open order contains `badge-overdue` and not `badge-cancelled`; a cancelled order contains `badge-cancelled` and not `badge-overdue`; product detail contains `section-nav`, `href="#purchase-orders"`, and an Audit span that is not an `<a>`; a low-stock product's on-hand card has the low class; served `site.js` contains the hash handler; existing status words ("Partially received", "Submitted", "Draft") stay.

## Acceptance criteria

- [x] PO detail matches `/Dev/Overhaul` detail: lines, summary, supplier, activity-style order details.
- [x] The product section nav marks the right link when the hash changes.
- [x] `dotnet test` is green.

## Notes

FR-8, FR-9. Do not add new queries. Use values the page models already expose.

### As built

- PO detail: Lines card with a received-progress meter and percent per line, Receipts card, then Summary (total, units, outstanding), Supplier and Order cards in a side column. Below `80rem` the side column drops under the main card. The header carries the back link, the status chip, `badge-overdue` beside it for an open order past its date (never for a cancelled one), and the Actions section.
- Product detail: on-hand card (`--it-display`, `is-low` / `is-zero`), Details card, `.section-nav` (History, Suppliers, Purchase orders as `href="#..."`, Audit an inert "Soon" span), and History, Suppliers and Open purchase orders as cards whose ids are the nav targets. `site.js` `markSectionNav()` sets `aria-current="true"` for the current hash on `hashchange`, and marks the first link when the hash is empty.
- Supplier detail: Details, Recent purchase orders (with "New purchase order" in the card head) and Products cards.
- No new queries; every value comes from the existing page models.
- Tests added: overdue vs cancelled chips on the PO page, the product section nav and its inert Audit label, on-hand tone for zero / low / healthy stock, and the `site.js` hash handler.
- `/Dev/Overhaul` was retired in WI-09-8, so "matches `/Dev/Overhaul` detail" was checked against the sample while it existed and is now carried by the live pages.
