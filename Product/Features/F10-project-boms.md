# F10 — Projects & BOMs

- **Status:** In Progress
- **Depends on:** F02, F03, F04, F05, F09
- **Roles:** Admin/Staff create and edit projects and BOMs, create parts, add shortages to purchase orders and record builds; only Admin changes a project number; Viewer reads projects and BOM status

## Summary

Many jobs need a known list of parts: build a unit, fit out a site, assemble a kit. A **project** holds a **bill of materials (BOM)**: catalog products with the quantity each unit needs. The BOM can use parts that already exist in the catalog or create new parts on the spot. A **BOM status check** compares what the project still needs with what is on hand and on order, so Staff can see at a glance whether the project can be built, and which missing parts still have to be ordered. Short parts can be added to a Draft purchase order that the user picks (or to a new draft for a supplier the user picks). When the parts are in, **Build** issues them from stock in one step and records the build.

This feature adds no new stock mechanism. Stock changes only through `StockService` (architecture invariant 1), and purchase orders change only through `PurchaseOrderService`.

## User stories

- As Staff, I want to create a project and list the parts it needs, picking from the catalog, so that the whole job is written down once.
- As Staff, I want to add a part that is not in the catalog yet while I am building the BOM, so that I do not leave the page to create it first.
- As any user, I want to see whether a project is ready to build, covered by open orders, or short, and which parts are short and by how many.
- As any user, I want each part to show what is in stock, on order and already in a draft PO (whoever made it), so that I do not order the same part twice.
- As Staff, I want, for a part that is already in a draft PO, to choose per line whether to add it anyway, skip it or update the quantity on that draft.
- As Staff, I want every project to get a project number automatically, so that other systems can refer to it later.
- As Staff, I want to send the short parts to a purchase order I choose, so that I can reuse a draft I already have open or start one for a specific supplier.
- As Staff, I want to record "built N units" and have the parts issued from stock in one action, so that stock stays correct and I do not issue a dozen parts by hand.
- As any user, I want to see on a product which projects use it, so that I know why it matters before I archive or reorder it.

## Functional requirements

1. FR-1 Project. Fields: Name (required, unique, max 100), **Number** (the project number, for example `PRJ-2026-0001`: generated automatically when the project is created, using the same per-year sequence mechanism as PO numbers with a configurable prefix in Admin settings; the user may type their own instead, max 30, unique, case-insensitive; always present; **only an Admin can change it afterwards** (a separate "Change number" action with a warning that outside systems may refer to the old number; Staff see it read-only); searchable and shown next to the name wherever the project appears), Description (max 500), **TargetUnits** (whole number >= 1, default 1: how many units the project is meant to produce), Notes (max 2000), IsActive. Projects are archived, not deleted, once they have a build; a project with no builds may be deleted. Optimistic concurrency via `RowVersion`.
2. FR-2 BOM line. A project has zero or more lines; each is an **active catalog product** plus **QuantityPerUnit** (whole number >= 1) and an optional Note (max 200, for example a position or reference). One line per product per project: adding a product that is already in the BOM is an error that names the existing line. Lines can be edited in place and removed at any time (builds keep their own history).
3. FR-3 New part from the BOM. The BOM editor offers "New part": a short form (SKU, Name, Unit defaulting to "each", Unit cost optional) that creates a catalog product through `ProductService.CreateAsync` and adds it to the BOM in one step. All catalog validation applies (unique SKU, barcode rules). The new product starts with zero stock and is a normal catalog product from then on.
4. FR-4 BOM status, per line. Let `remaining = max(0, TargetUnits − built so far)`.
   - needed = QuantityPerUnit x remaining
   - on hand = `Product.QuantityOnHand`
   - on order = Σ(QuantityOrdered − QuantityReceived) over Submitted / PartiallyReceived PO lines (the same figure low-stock uses)
   - **in draft** = Σ QuantityOrdered over lines of **every Draft PO, whichever supplier or project it was made for**, plus the numbers of those POs (each a link). A draft is not an order yet, so this quantity is shown separately and is never part of "on order".
   - short = max(0, needed − on hand)
   - to order = max(0, short − on order) (drafts do not reduce it, so the figure stays honest if a draft is later deleted)
   - Line state: **Ready** (on hand >= needed), **Covered** (on hand + on order >= needed), **In draft** (on hand + on order + in draft >= needed), otherwise **Short**.
   - Every line shows its three sources side by side, so the user always sees where a part stands: **in stock / on order / in PO draft** (with the draft PO numbers). A line with a draft quantity but not enough to cover the need is still Short, and says "n in draft PO-…".
   - A line whose product has been archived is flagged "Archived part". It still counts in the maths, but cannot be added to a PO until the product is restored.
5. FR-5 BOM status, per project. The project state is the worst line state (Short, then In draft, then Covered, then Ready). A project with no lines is **Empty**; a project whose remaining units are 0 is **Built**. The same state chip appears on the project list and the project page. The project page header also summarises, for example, "3 short, 2 in draft PO, 1 on order".
6. FR-6 Order shortages. From a project the user opens an "Order shortages" review that lists every line with `to order > 0`. Each line shows in stock, on order and **in PO draft** (with links to those drafts), so the user cannot order a part twice without seeing it. Each line has an **action** and a quantity:
   - A part that is in no draft: **Add** (default) or **Skip**. The quantity defaults to `to order`, raised to the supplier link's `MinOrderQuantity` when the target PO's supplier has one.
   - A part that is already in one or more drafts: **Skip**, **Update quantity** or **Add**. Skip is the default when the drafts already cover `to order`; Update quantity is the default when they cover only part of it. *Update quantity* changes the quantity on the existing draft line to the number entered (prefilled with its current quantity plus the shortfall that is still uncovered, rounded to the minimum order quantity); when several drafts hold the part the user picks which draft to update (the chosen target if it holds the part). *Add* puts the part on the chosen target as well, and is not offered when the chosen target already holds the part (use Update instead). The row says "Already in draft PO-…" with a link, so the choice is always informed.

   The user then picks the target for the Add lines (not needed when no line is set to Add):
   - an **existing Draft PO** (the list shows number, supplier and line count), or
   - **a new Draft PO** for a supplier chosen from the active suppliers.

   Lines are priced by the existing PO rule (supplier link price, else product cost). The result is all-or-nothing for validation errors (for example a target that is no longer a Draft) and reports **added**, **updated** (old quantity to new) and **skipped** lines. Archived parts are skipped and reported. Only Draft POs can be changed: an issued PO (Submitted or later) is never touched, and if a draft was submitted after the review page was opened, the whole post is refused with nothing applied. Nothing is submitted automatically.
7. FR-7 Build. "Build N units" (N >= 1; defaults to the remaining units, never prefilled above what stock allows) is available to Admin/Staff. It checks every line for QuantityPerUnit x N on hand and, if any are short, refuses with one error per short part (nothing is posted). Otherwise it posts one `Issue` stock transaction per line through `StockService` in a single call, with Reason "Build" and Reference `<project name> build #<build id>`, and records a `ProjectBuild(ProjectId, Units, UserId, Note)` row in the same database transaction. Builds cannot be edited. Built so far = Σ `ProjectBuild.Units`. A mistake is corrected by reversing the individual issue rows from the stock history, as for any other issue.
8. FR-8 Project list. A list card (F09 FR-4) with Active / Archived tabs, search by name or number, and columns: number, name, parts, target, built, state chip, BOM cost. Default sort by name. "New project" is the one primary button in the page header.
9. FR-9 Project page. Page header with back link, state chip and actions (Edit; Order shortages when any line is short; Build; overflow menu: Archive / Restore, Delete when there are no builds). Body: the BOM card as the status table (part, per unit, needed, on hand, on order, short, state), a Summary card (BOM cost = Σ QuantityPerUnit x `UnitCostCents`, built so far, remaining), and a Builds card (date, units, who). A one-line note states that stock is not reserved per project, so other projects may need the same parts.
10. FR-10 Product page. A read-only "Used in projects" card lists active projects whose BOM contains the product (project, quantity per unit). The product's existing on-order information also shows the quantity **in draft POs**, with links to those drafts, so the three sources (in stock / on order / in PO draft) read the same everywhere. No new action.
11. FR-11 Cost visibility. The BOM cost total (Σ QuantityPerUnit x `UnitCostCents`, per unit and for the remaining units) is visible to **every signed-in user, Viewers included**. The same decision applies to F06 stock valuation.
12. FR-12 UI. All screens use the F09 patterns (page header, list card, button roles, overflow menu, confirm dialog, form sections, field errors, status chips). The state chips reuse the existing chip classes: Ready = `badge-ok`, Covered = `badge-partial`, In draft = `badge-draft`, Short = `badge-out`, Built = `badge-received`, Empty = `badge-inactive`; confirm the pairings in the `/Dev/Components` gallery. Destructive actions confirm in the app dialog. Every action is a form that also posts without JavaScript.

## Business rules

- Quantity on hand changes only through `StockService`. A build writes `Issue` transactions and never edits `Product.QuantityOnHand` itself.
- Negative on-hand stays blocked (architecture invariant 3). A build never overrides it.
- PO lines are added only to a Draft, only through `PurchaseOrderService`; one line per product per PO is kept.
- Status is a live calculation. Nothing about it is stored, so it always agrees with the current stock and orders.
- Draft PO quantities are information, not supply: they never make a line "Ready" or change "to order", and they only turn a Short line into "In draft" when they would cover it.
- The project **Id** is the stable key for relationships inside the database. The project **Number** is the stable, unique key for outside systems: it is generated or entered when the project is created and afterwards changed only by an Admin, on purpose (FR-1). The sequence never reissues a number it has handed out, so an automatically generated number is never a duplicate; a changed-away number can only come back if an Admin types it.
- Purchase orders that have left Draft (Submitted, PartiallyReceived, Received, Cancelled) are never changed by F10. Order shortages only adds to or updates Draft POs. F10 stores no link between a PO and a project, so changing a project number touches no PO.
- A part already in a draft PO is never added a second time to that same PO; the user instead chooses Update quantity.
- Stock is **not reserved** by a project in v1. Two projects can both show Ready for the same stock.
- Archiving a product that is in a BOM is allowed (catalog rules are unchanged); the BOM shows "Archived part".
- Reference data is soft-deleted: projects with builds are archived, never deleted; BOM lines and builds are never edited after the fact except lines, which are removable.
- Money is `long` cents, quantities are whole `int`, times are UTC.

## Data model

New tables (see architecture section 5):

- `Project(Id, Name UQ, Number UQ (NOCASE, required; changed only by an Admin), Description, TargetUnits, Notes, IsActive, RowVersion, CreatedAt, UpdatedAt)`
- `BomLine(Id, ProjectId → Project, ProductId → Product, QuantityPerUnit, Note, CreatedAt, UpdatedAt)`, unique `(ProjectId, ProductId)`, index on `ProductId`
- `ProjectBuild(Id, ProjectId → Project, Units, UserId → AspNetUsers, Note, CreatedAt)`

A `DocumentSequence` row named "Project" (per year) numbers projects; no schema change is needed for it.

`StockTransaction` is **not** changed: the link from a build to its issue rows is the `Reference` text, which avoids the table rebuild D16 requires (decision D18).

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Projects` | List (tabs, search, state chips) | CanView |
| `/Projects/{id}` | Project page: BOM status, summary, builds | CanView |
| `/Projects/Edit/{id?}` | Create / edit project | CanEdit |
| `/Projects/{id}?handler=AddLine` / `UpdateLine` / `RemoveLine` (POST, HTMX) | BOM line editing | CanEdit |
| `/Projects/{id}?handler=NewPart` (GET modal, POST) | Create a catalog part and add it to the BOM | CanEdit |
| `/Projects/{id}/Order` (GET review, POST) | Add shortages to a chosen draft PO or a new draft | CanEdit |
| `/Projects/{id}?handler=Build` (GET modal, POST) | Build N units: issue parts, record the build | CanEdit |
| `/Projects/{id}?handler=Archive` / `Restore` / `Delete` (POST) | Lifecycle | CanEdit |
| `/Projects/{id}?handler=ChangeNumber` (GET modal, POST) | Change the project number | RequireAdmin |

## Out of scope

- Reserving or allocating stock to a project, or showing competing demand from other projects (possible follow-up: an "also needed by N other active projects" hint).
- Sub-assemblies (a BOM inside a BOM), alternate or substitute parts, BOM revisions and versions.
- BOM CSV import or export (F07 can add it), emailing BOMs, cost history.
- A public API (or any other integration) for outside systems to read BOMs by project number. That belongs to the architecture-level API question, not to F10. F10 only guarantees what such an API would need: a unique, indexed project number that only an Admin can change and BOM data available through `ProjectService`.
- Reversing a whole build in one click. Individual issue rows can be reversed from the stock history.
- Fractional quantities (same limit as the rest of v1).

## Work items

Built in this order: schema, API, then the frontend in two steps (the frontend is the largest part, as in F09).

| ID | Title | Status |
|---|---|---|
| [WI-10-1](../work-items/F10-project-boms/WI-10-1-db-schema.md) | Project, BomLine, ProjectBuild schema | Done |
| [WI-10-2](../work-items/F10-project-boms/WI-10-2-api.md) | ProjectService (BOM, status, build), PO add-lines | Done |
| [WI-10-3](../work-items/F10-project-boms/WI-10-3-frontend-projects.md) | Projects list, project page, BOM editing, new part | Done |
| [WI-10-4](../work-items/F10-project-boms/WI-10-4-frontend-order-build.md) | Order shortages, build, builds history | Planned |

## Open questions

Resolved: Draft PO quantities are shown as a third source ("in PO draft") on every line and on the order review, without counting as supply (FR-4, FR-6). Viewers see BOM cost totals and F06 valuation (FR-11). A project has a project number, generated like PO numbers and changeable only by an Admin (FR-1). Purchase orders that have been issued (anything past Draft) are never changed by F10; Update quantity works on Drafts only (FR-6). When a part is already in a draft the user chooses Add, Skip or Update quantity per line (FR-6). Outside access by project number is out of scope for F10.

- Should a changed project number keep a history of the old numbers (so an outside system holding the old one can be told it moved)? Today F08 (audit log) is the only record of the change.
- Relationships to be added later (to be specified in their own feature): which entities should point at a project?
