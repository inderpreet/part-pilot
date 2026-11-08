# F09 — UI overhaul

- **Status:** Done
- **Depends on:** F05
- **Roles:** Every signed-in role sees the same chrome. Actions stay behind the policies those pages already enforce (CanView, CanEdit, RequireAdmin). This feature adds no permission.

## Summary

F00–F05 each styled their own screens on top of Pico.css. The result works, but it reads as a stack of forms: a text nav strip, a title that sits apart from its actions, six equal buttons, native confirm boxes, and type that grows by 25% on wide screens. This feature replaces the look and the layout of those screens with one product design: a left rail, a quiet top bar, white cards on a light canvas, one solid primary action per page, soft status chips, an in-app confirm dialog, and sectioned forms. It changes presentation only. Later features (F06, F07, F08) use these patterns.

The target was a working prototype page, `/Dev/Overhaul` (WI-09-0), shown on sample data. Each later work item made the real screens match it, and the prototype was then retired (WI-09-8). `/Dev/Components` is now the gallery of the shared patterns.

## Design direction

Stay on vendored Pico.css 2.1.1 and `site.css`. No webfont, no component library, no new package. The system UI font is the font. Light theme only (`data-theme="light"` stays).

- Light canvas (`#f5f6f8`). White rail on the left, white cards with a 1px hairline border and a 12px radius.
- One accent (indigo `#4f46e5`). Everything else is neutral, plus four status colours.
- One primary button per page. Secondary actions are white and outlined. Destructive actions sit in a "⋯" menu and always confirm.
- Status is a soft tinted chip with a dot. Overdue is the only solid chip.
- Lists are cards: tabs, a filter row, a table with a sticky header, a footer with the pager.
- Forms are sections: a title and description on the left, the fields in a card on the right, a sticky save bar.

## User stories

- As any user, I want one place to see where I am and to move between sections, with the current section obvious.
- As any user, I want the page title, its status and its main action together, so I don't scan the page for "New" or "Save".
- As Staff receiving goods on a tablet, I want destructive actions out of the way and confirmed in the app's own dialog, so I don't tap Cancel when I meant Receive.
- As any user, I want a long list to keep its column headings in view and its filters on one row.
- As any user, I want a form error next to the field it belongs to, and the form split into short sections.
- As any user, I want lists and figures in even, tabular type so more rows fit, while forms stay large enough to read and tap.

## Functional requirements

1. FR-1 Tokens and type. `site.css` defines the tokens on `:root` (colour, radius, shadow, `--it-text-*`). It pins `--pico-font-size: 100%` on `:root`, because Pico raises the root size to 125% as the window widens. The font is the system UI stack already named in `--pico-font-family`. No font file.

    | Token | Size | Use |
    |---|---|---|
    | `--it-text-xs` | `0.75rem` | Chips, column headings, rail group labels. Not for sentences. |
    | `--it-text-sm` | `0.875rem` | Tables, buttons, nav, meta text |
    | `--it-text` | `1rem` | Body, form controls |
    | `--it-text-lg` | `1.125rem` | Dialog and section titles |
    | `--it-title` | `1.5rem` | The page `<h1>`, at every width |
    | `--it-display` | `2rem` | Stat and on-hand numbers |
    | `--it-qty` | `1.25rem` | Receive-page quantity input only |

    `--pico-line-height` is `1.45`. Tables use `1.3`. `.num`, money and number inputs use `font-variant-numeric: tabular-nums`. The print sheet stays `11pt`. Form controls are never smaller than `1rem`.
2. FR-2 Shell. `_Layout` renders a skip link ("Skip to content", first focusable, targets `<main id="content">`), a left rail, and a top bar.
    - Rail: the company name with a mark at the top. Items are an icon and a label, grouped: Dashboard; Inventory (Products, Stock, Suppliers, Purchase Orders); Insights (Reports). Admin sits at the bottom above the user card. The user card shows an avatar with initials, the name, the role, and Sign out.
    - Every `NavItem` in `NavItems.All` still renders. `Available: true` is a link; `Available: false` is an inert muted label with a "Soon" tag (`aria-disabled="true"`, `title="Coming soon"`, not an `<a>`). The current item has a soft fill and a left accent bar (`aria-current="page"`).
    - Top bar: breadcrumb (section, then the page crumb when the page sets one) and a menu button on narrow screens. A global search box is not part of this feature (see Out of scope).
    - Anonymous pages (login, setup, error) get no rail and no top bar.
3. FR-3 Page header, rendered by `_Layout` from `ViewData` and optional sections:
    - `<h1>` is the `ViewData["Title"]` text. The document title stays `Title · Company`.
    - Optional `Leading` section (back link above the title), `Badges` (chips next to the title), `Subtitle` (a muted line under it), and `Actions` (right-aligned, wrapping: secondary buttons, then the primary, then the overflow menu).
    - `ViewData["Header"] = "plain"` (sign-in, setup, change password, access denied, error) renders the `<h1>` only, centred with the card.
    - `_PrintLayout` is unchanged.
4. FR-4 Lists. Products, categories, suppliers, stock movements, stock count, purchase orders and users render inside one `.card.list-card`: optional status tabs (`.tabs`, with `.tab-count` where the page already knows the counts), one filter row (`.toolbar`), the table region, and a footer with the pager. The "New …" action lives in the page header, not in the filter row. Search, selects and dates keep their query names and htmx behaviour.
    - The table region scrolls inside the card with a sticky `thead`. Numeric columns, including sort headers, are right-aligned.
    - A 2px `.load-bar` (accessible name "Updating") appears at the top of the card while an htmx request is in flight (`hx-indicator`). It adds no text and does not shift the table.
    - Archived and inactive rows are muted.
5. FR-5 Button roles, applied to existing actions (no new operations):
    - **Primary** (solid accent): the commit action of a page: Save, Create, Submit order, Receive goods, one "New …" per list. One per header.
    - **Secondary** (white, outlined; markup `class="secondary outline"` or `.btn-secondary`): Print, Edit, Back, Cancel (leaving a form), Manage categories, Receive all / Clear all.
    - **Text** (`.text`, no border): low-emphasis actions such as form Cancel.
    - **Danger**: Archive, Cancel order, Close short, Reverse, Delete, and the stock-verify fix. On a detail page they live in a `⋯` overflow menu (`<details class="menu">`) after the primary and secondary buttons. In a table row they are a small text button in the danger colour. A permanent delete confirms with a solid danger button.
    - Restore stays secondary.
6. FR-6 Confirm dialog. `site.js` handles `htmx:confirm`: it opens `<dialog id="confirm">` (in `_Layout`) with the `hx-confirm` text, a Cancel button and a Confirm button. Cancel is focused when the triggering control is danger (inside `.menu` or marked `.danger`, which covers the `text danger` row actions); otherwise Confirm is focused. The Confirm button takes the source button's label and is red for danger sources. Escape and Cancel abort the request. Confirm continues it. The dialog is not `window.confirm`. A post without JavaScript still submits the form and skips the dialog. `#modal` stays a separate element.
7. FR-7 Forms. Data-entry forms group fields into `.form-row` sections (title and description left, a card of fields right): product (Identity, Stock, Cost), supplier (Contact, Notes), PO header (Supplier and dates, Notes), user, settings. The actions sit in a sticky `.action-bar` at the bottom: Cancel (text) and the one primary. For a ModelState key that matches a rendered control, the message is also rendered after that control (`.field-error`, linked with `aria-describedby`) and the control gets `aria-invalid="true"`. The summary at the top stays, restyled as `.alert.alert-error`, for form-level errors and as the list of everything. No new validation rules.
8. FR-8 Detail pages. Product, supplier and PO detail use the page header (FR-3) and cards. PO detail: a Lines card as the main column; Supplier, Summary (total, received, outstanding) and Order details cards beside or below it; Receipts below. Product detail keeps the on-hand number (`--it-display`, coloured when low or zero) and replaces the tab row with an in-page nav (`.section-nav`) of History, Suppliers, Purchase orders, with `aria-current` on the link for the current hash (`site.js` updates it on `hashchange`; History when the hash is empty). Audit stays an inert "Soon" label.
9. FR-9 Status chips. Same class names, new look: a tinted pill with a leading dot.

    | Class | Use | Background | Text |
    |---|---|---|---|
    | `badge-submitted` | Submitted | `#eff8ff` | `#175cd3` |
    | `badge-partial`, `badge-low` | Partially received, Low stock | `#fffaeb` | `#93370d` |
    | `badge-received`, `badge-ok` | Received, In stock | `#ecfdf3` | `#067647` |
    | `badge-cancelled`, `badge-out` | Cancelled, Out of stock | `#fef3f2` | `#b42318` |
    | `badge-draft`, `badge-inactive` | Draft, Archived / inactive | `#f2f4f7` | `#475467` |
    | `badge-overdue` | Open PO past its expected date | `#b54708` | `#fff` |

    Overdue no longer uses `badge-cancelled`, and shows beside the status chip, not instead of it. Each pair is at least 4.5:1.
10. FR-10 Narrow and tablet. Below `64rem` the rail becomes an off-canvas drawer opened by the top-bar menu button (scrim, Escape and link click close it). Without JavaScript the rail stays visible above the content. Below `80rem` side columns drop under the main card (detail pages). Below `40rem` the header actions stack, filters go full width, and tables scroll inside the card. At `(pointer: coarse)` buttons, menu items and rail links have a minimum hit size of `2.75rem`.
11. FR-11 Print. The PO print view is unchanged and has no app chrome. The rail, top bar, filters, buttons, dialogs and toasts are hidden when printing any other page.
12. FR-12 Reference. `/Dev/Components` (Development and Testing only, `CanView`) is the gallery of the shared patterns on the real stylesheet: button roles, overflow menu and confirm dialog, status chips, card / stat / key-value, timeline, tabs and section nav, alert and field error, plus the older partials. The `/Dev/Overhaul` prototype page and its `overhaul.css` were deleted in WI-09-8, so `site.css` is the only app stylesheet.

## Business rules

- No data, route, query-string or authorization change. A restyle that breaks a filter name, a handler name or a role check is a defect.
- Unbuilt nav entries stay visible as inert labels. They are not links and not removed. The feature that adds the first page of a section still sets `Available: true`.
- Type comes from the FR-1 tokens. A later screen does not introduce a new `font-size` or a font file.
- Confirmation stays progressive enhancement. Tests that post a form through `HttpClient` (no JavaScript) keep working.
- One primary button per page header. A list page's "New …" is that button. A detail page's commit action (Submit, Receive) is that button.
- Danger styling is visual. The server rules for who may delete, cancel, reverse or archive do not change.
- Existing class names and `data-*` hooks used by `site.js` and by tests keep working unless a work item says otherwise.

## Data model

No new entities, columns or settings.

## Screens / endpoints

No new product routes. Every screen from F00–F05 changes:

| Route | What changes | Policy |
|---|---|---|
| `/` | Shell. Dashboard content stays the F06 placeholder, in the new card style | CanView |
| `/Account/Login`, `/Account/Setup`, `/Account/ChangePassword`, `/AccessDenied`, `/Error` | Plain header, no rail | existing |
| `/Products`, `/Products/{id}`, `/Products/Edit`, `/Categories` | Header, list card, form sections, chips, section nav | existing |
| `/Stock`, `/Stock/Movements`, `/Stock/Count`, `/Stock/Bulk`, `/Stock/Move` | Header, list card, forms, confirm | existing |
| `/Suppliers`, `/Suppliers/{id}`, `/Suppliers/Edit` | Header, list card, forms, cards | existing |
| `/PurchaseOrders`, `/PurchaseOrders/{id}`, `/PurchaseOrders/Edit`, `/PurchaseOrders/{id}/Receive` | Header, list card, forms, overflow menu, chips | existing |
| `/PurchaseOrders/{id}/Print` | Unchanged sheet, no app chrome | CanView |
| `/Admin`, `/Admin/Users`, `/Admin/Settings`, `/Admin/VerifyStock` | Header, list card, forms, danger styling on the verify fix | existing |
| `/Dev/Components` | Gallery of the shared patterns | Development and Testing |

## Out of scope

- Dashboard content (stat cards, low-stock list, activity feed). F06 builds it with the `.stat`, `.card` and `.timeline` classes this feature adds. `/Dev/Components` shows the pieces.
- A global search box in the top bar. A later feature, once there is a cross-entity search to call.
- Dark theme, density toggle, user-saved preferences.
- A downloaded or embedded font file. Rewriting pages as a component framework, or upgrading Pico.
- New product functionality (bulk edit, saved filters, column picker, shortcut map).
- Changing the PO print layout.
- Email, notifications, or anything in F06, F07, F08 beyond "use these patterns".

## Work items

Build in this order. Each item leaves the app working and the tests green.

| ID | Title | Status |
|---|---|---|
| [WI-09-0](../work-items/F09-ui-overhaul/WI-09-0-reference.md) | Reference page (`/Dev/Overhaul`) | Done |
| [WI-09-1](../work-items/F09-ui-overhaul/WI-09-1-foundation.md) | Foundation: tokens, type, controls, chips, tables | Done |
| [WI-09-2](../work-items/F09-ui-overhaul/WI-09-2-shell.md) | Shell: rail, top bar, skip link, drawer | Done |
| [WI-09-3](../work-items/F09-ui-overhaul/WI-09-3-page-header.md) | Page header | Done |
| [WI-09-4](../work-items/F09-ui-overhaul/WI-09-4-lists.md) | List cards | Done |
| [WI-09-5](../work-items/F09-ui-overhaul/WI-09-5-actions.md) | Button roles, overflow menu, confirm dialog | Done |
| [WI-09-6](../work-items/F09-ui-overhaul/WI-09-6-forms.md) | Form sections and field errors | Done |
| [WI-09-7](../work-items/F09-ui-overhaul/WI-09-7-detail.md) | Detail pages | Done |
| [WI-09-8](../work-items/F09-ui-overhaul/WI-09-8-finish.md) | Narrow screens, print, gallery, clean-up | Done |

## Open questions

- A global search box (top bar) and the dashboard content are deferred to later features.
- Dark theme is a later pass.
