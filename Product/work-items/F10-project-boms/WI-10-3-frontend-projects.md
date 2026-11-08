# WI-10-3 — Projects list, project page, BOM editing, new part

- **Feature:** [F10 — Projects & BOMs](../../Features/F10-project-boms.md)
- **Status:** Done
- **Depends on:** WI-10-2, WI-09-8
- **Estimate:** L

## Goal

Users can create projects, build a BOM from catalog parts or brand-new parts, and read the BOM status check on the project page. Ordering shortages and building come in WI-10-4.

## Scope

- In: nav entry and icon, `/Projects` list, `/Projects/{id}` page, `/Projects/Edit/{id?}`, HTMX BOM line editing, the New part modal, archive / restore / delete, the "Used in projects" card on the product page, tests.
- Out: Order shortages and Build (WI-10-4). The page header shows no placeholder buttons for them yet.

## Tasks

- [x] Nav: flip a "Projects" entry in `Pages/Shared/NavItems.cs` to `Available: true` (Inventory group, after Purchase Orders); add a matching icon symbol to `Pages/Shared/_Icons.cshtml`.
- [x] `/Projects` (`CanView`): one `.card.list-card` with Active / Archived tabs, search box (matches name or number), and the table (number, name, parts, target, built, state chip, BOM cost). Same htmx full-page-or-partial behaviour as Products. "New project" is the primary button in the page header (`CanEdit` only). Empty state invites creating the first project.
- [x] `/Projects/Edit/{id?}` (`CanEdit`): sectioned form (Details: name, project number (on create: optional, hint "leave blank to generate PRJ-yyyy-nnnn"; on edit: shown read-only for everyone; only an Admin changes it, through "Change number"), target units, description; Notes) with `Html.FieldError` / `Html.DescribedBy`, sticky `.action-bar`, optimistic concurrency conflict message.
- [x] `/Projects/{id}` (`CanView`):
  - [x] Page header: back link to Projects, name and project number, state chip with a one-line summary ("3 short, 2 in draft PO, 1 on order"), Actions section (Edit; overflow menu with Archive / Restore, Delete when there are no builds, and, for Admins only, "Change number…", each confirming in the app dialog).
  - [x] BOM card as the status table: part (links to the product), per unit, needed, **in stock, on order, in PO draft** (draft quantity with the draft PO numbers as links, shown even when the line is Ready), short, state chip (Ready / Covered / In draft / Short); sticky header; archived-part flag; empty state when there are no lines.
  - [x] Add-part row using the existing `_ProductPicker` (active products) with a quantity field and Add, plus a "New part" button that opens a modal.
  - [x] Inline quantity edit and Remove (`text danger`, confirms) per line, via HTMX partials; the state chip and totals refresh after each change.
  - [x] Summary card (`.kv`): BOM cost per unit and for the remaining units (visible to Viewers too), target units, built so far, remaining. One muted line: stock is not reserved per project.
  - [x] Builds card (date, units, who). Empty until WI-10-4.
- [x] "Change number" (Admin only, `RequireAdmin` on the handler): GET `?handler=ChangeNumber` opens a modal showing the current number, a field for the new one, and the warning "Other systems may refer to the current project number. Purchase orders are not affected." POST calls `ProjectService.ChangeNumberAsync`; a duplicate or too-long number shows the field error in the modal; success flashes "Project number changed from X to Y" and refreshes the header. Works as a plain form post; Staff and Viewer get no menu item and a 403 on a direct request.
- [x] New part modal (`_NewPartModal.cshtml`, own `#modal` form): SKU, Name, Unit (default "each"), Unit cost (optional), Quantity per unit. Posts to `?handler=NewPart`, shows field errors in place (HTTP 422 swap), closes and refreshes the BOM on success.
- [x] Product page (`/Products/{id}`): "Used in projects" card (project, quantity per unit), read-only, hidden when there are none. The existing on-order information on the product page also shows "In draft POs: n" with links to those drafts (`GetInDraftByProductAsync`), so stock / on order / in draft read the same everywhere.
- [x] Admin settings (`/Admin/Settings`): a "Project number prefix" field beside the PO prefix (max 10, default `PRJ-`), saved through `SettingsService`; update the settings page tests.
- [x] Every action is a plain form that also works without JavaScript; htmx only enhances. Antiforgery token handling follows the layout convention.
- [x] Authorization: `[Authorize(Policy = Policies.CanView)]` on read pages, `CanEdit` on edit pages and mutating handlers. A Viewer sees no edit controls and gets 403 on a direct post.
- [x] Tests (`WebApplicationFactory`, `AppFactory`, `CreateClientAs`):
  - [x] Smoke test per page; list full page vs htmx partial; search and tabs.
  - [x] Add a catalog part, add the same part again (error names the line), edit and remove a line.
  - [x] New part creates a catalog product and a line; a duplicate SKU shows the field error and creates nothing.
  - [x] The status table shows Ready, Covered and Short for seeded stock and a submitted PO, and the project chip is the worst line.
  - [x] Viewer cannot mutate (role matrix); anonymous is redirected to sign-in; Projects nav entry is a link now.
  - [x] The status table shows in stock / on order / in PO draft side by side, with the draft PO numbers as links; a part that is in a draft made for another project or supplier is shown; a part in no draft shows none.
  - [x] A line covered only by a draft shows the In draft chip, and the project chip follows the order Short > In draft > Covered > Ready.
  - [x] Project number: blank on create generates the next `PRJ-<year>-nnnn`; a typed number is kept; a duplicate shows the field error; the edit form shows it read-only and a posted change to it is ignored; an Admin can change it through the modal, a duplicate shows the field error, Staff and Viewer get 403 and no menu item, and the old number no longer finds the project; list search finds a project by number; number shown in list and header.
  - [x] A Viewer sees the BOM cost totals.
  - [x] Product page shows "Used in projects" only when applicable, and "In draft POs" with links when the product is on a draft.

## Acceptance criteria

- [x] A user can create a project, add catalog parts and a new part, and see correct per-line and per-project status.
- [x] Editing a BOM never reloads the whole page for htmx requests and works with a plain form post.
- [x] Viewers can read (including costs) but not change anything.
- [x] Every BOM line says where its part stands: in stock, on order and in PO draft.
- [ ] At 390px and 768px there is no horizontal page scroll (the BOM table scrolls inside its card), and the status chips and actions stay reachable.
- [x] `dotnet build` has zero warnings and `dotnet test` is green.

## Notes

Follow the F09 patterns: `_Layout` page-header sections, `.list-card`, button roles, `.menu`, `.detail-cols` / cards, `.form-row`, state chips (`badge-ok` Ready, `badge-partial` Covered, `badge-draft` In draft, `badge-out` Short, `badge-received` Built, `badge-inactive` Empty; confirm the pairings in `/Dev/Components`). Look at `Pages/Suppliers/*` and `Pages/PurchaseOrders/*` for the closest existing examples. Add a short "As built" section when done.

## As built

- Pages (`Pages/Projects/`): `Index` (+ `_ProjectResults`), `Edit`, `Detail`, with partials `_Bom`, `_ProjectChip`, `_ProjectSub`, `_ProjectSummary`, `_NewPartModal` / `_NewPartForm`, `_ChangeNumberModal` / `_ChangeNumberForm`. Shared: `ProjectDisplay`, `_ProjectStateBadge`, `_LineStateBadge`, the Projects nav entry (`NavItems`, `clipboard` icon).
- A BOM change (add, save, remove, New part) answers with `_Bom` (id `#bom`), and the state chip, the line under the title and the summary card ride along as out-of-band swaps (`hx-swap-oob`), so nothing reloads. Failures answer 422 with the messages in the `#bom` card.
- New part: the form replaces itself on a 422. On success the server sets `HX-Retarget: #bom`, `HX-Reswap: outerHTML` and `HX-Trigger: closeModal`, so one response refreshes the BOM and closes the dialog.
- Without JavaScript, `?handler=NewPart` and `?handler=ChangeNumber` open the same form as a card at the top of the project page (`#inline-dialog`); a good post redirects back to the project and a failure re-renders the page with the field errors.
- Change number is checked with the `RequireAdmin` policy inside the handler (the page itself is `CanView`); Staff and Viewer get 403 and no menu item. Success flashes "Project number changed from X to Y." and redirects, so the header shows the new number.
- Mutating handlers on the project page (`AddLine`, `SaveLine`, `RemoveLine`, `NewPart`, `Archive`, `Delete`) check `CanEdit` themselves (the page is `CanView` so Viewers read costs). The edit page and the list's "New project" button are `CanEdit`.
- Added beyond the task list: `IProjectService.GetProjectsUsingProductAsync` (`ProductProjectUse`) for the product page; a "Projects" jump link beside "Purchase orders" on the product page when the card shows. The "Admin settings" task was already done in WI-10-2.
- Archived project: the BOM stays editable (the service does not block it); only the Archived badge and the Restore menu item differ.
- Tests: `ProjectPagesTests` (28 tests) covering every item in the list above.
- Not done here: the 390px / 768px visual check. It needs the finished page (Order shortages and Build arrive in WI-10-4), so it runs once as part of the F10 close-out. The BOM table reuses the `.table-wrap` card scroll and the `.detail-cols` collapse that the PO pages use.