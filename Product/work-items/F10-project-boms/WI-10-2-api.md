# WI-10-2 — ProjectService (BOM, status, build), PO add-lines

- **Feature:** [F10 — Projects & BOMs](../../Features/F10-project-boms.md)
- **Status:** Planned
- **Depends on:** WI-10-1, WI-03-2, WI-04-2, WI-05-2
- **Estimate:** L

## Goal

All project, BOM, status and build rules live in services and are tested. A project's BOM status is a pure calculation, shortages can be added to a chosen draft PO in one transaction, and a build issues parts through `StockService` atomically.

## Scope

- In: `ProjectService`, status calculation, quick-create part, shortage-to-PO orchestration, build, one new method on `PurchaseOrderService`, a small shared min-order rounding helper, DI registration, tests.
- Out: pages (WI-10-3, WI-10-4).

## Tasks

- [ ] Models in `Services/ProjectModels.cs`: `ProjectListQuery`, `ProjectListItem` (with parts count, built, state, BOM cost), `ProjectDetail`, `ProjectInput` (with an optional `Number`; blank means generate), `BomLineItem`, `BomLineInput`, `NewPartInput`, `BomStatus` / `BomLineStatus` (needed, on hand, on order, **in draft** with the draft PO ids and numbers, short, to order, `LineState`), `LineState` (Ready, Covered, InDraft, Short), `ProjectState` (Empty, Ready, Covered, InDraft, Short, Built), `ShortageTarget` (existing draft id, or supplier id for a new draft), `AddShortagesResult` (added, skipped with reason), `BuildResult`.
- [ ] `ProjectService` (one service per aggregate, registered like `AddPurchaseOrders()`):
  - [ ] `SearchAsync` (matches name or number), `GetAsync`, `CreateAsync` (generates the number when none is entered; a duplicate number is a field error on `Number`), `UpdateAsync(id, rowVersion, input)` (never changes `Number`), `ChangeNumberAsync(id, rowVersion, newNumber, ProjectActor actor)` (`ProjectActor(UserId, IsAdmin)` like `PurchaseOrderActor`; refused with `project.number_admin_only` unless `IsAdmin`; max 30, unique ignoring case, a duplicate is a field error on `Number`; a number equal to the current one is a no-op; bumps `RowVersion`; raises `ProjectNumberChanged(projectId, oldNumber, newNumber)` for F08), `SetActiveAsync`, `DeleteAsync` (refused when the project has builds).
  - [ ] `AddLineAsync` (active product only, quantity >= 1, one line per product with an error naming the existing line), `UpdateLineAsync`, `RemoveLineAsync` (idempotent).
  - [ ] `CreatePartAndAddAsync(projectId, NewPartInput, quantityPerUnit)`: call `ProductService.CreateAsync`, then add the line, in one transaction; a failed product create leaves no line.
  - [ ] `GetStatusAsync(projectId)`: per line `needed`, `onHand`, `onOrder` (from `PurchaseOrderService.GetOnOrderByProductAsync`), `inDraft` and its draft PO references (from `PurchaseOrderService.GetInDraftByProductAsync`), `short`, `toOrder` (drafts never reduce it), `LineState`, archived-part flag; project state is the worst line state (Short > InDraft > Covered > Ready), Empty for no lines, Built when remaining units are 0 (FR-4, FR-5). Remaining = max(0, TargetUnits − Σ built units).
  - [ ] `GetStatusSummariesAsync(projectIds)` for the list page: one batch of queries, not one status call per row.
  - [ ] `AddShortagesToOrderAsync(projectId, IReadOnlyList<ShortageDecision>, ShortageTarget? target, PurchaseOrderActor actor)` where `ShortageDecision(bomLineId, ShortageAction action, int quantity, long? purchaseOrderId)` and `ShortageAction` is `Add`, `UpdateQuantity` or `Skip`. `Add` goes to the target (an existing Draft, or a new draft created with `CreateDraftAsync` for a chosen supplier; the target is required only when some decision is `Add`, and no draft is created when none is); `UpdateQuantity` sets the quantity of the line on the named draft (`purchaseOrderId`); `Skip` does nothing. All changes go to `PurchaseOrderService.ApplyDraftChangesAsync` in one transaction (and the new draft, if any, joins it). Returns added, updated (old to new quantity) and skipped, each skip with its reason.
  - [ ] `BuildAsync(projectId, units, note, StockContext)`: one database transaction that (1) re-reads on-hand for every line, (2) refuses with one error per short part when any is short, (3) posts all `Issue` movements in a single `StockService.PostAsync` call with Reason "Build" and Reference `<project name> build #<id>`, (4) writes the `ProjectBuild` row. Use the same ambient-transaction pattern as `PurchaseOrderService.ReceiveAsync`, so a failure in either step rolls back both.
- [ ] `PurchaseOrderService.GetInDraftByProductAsync(IReadOnlyCollection<long>? productIds = null)`: for each product, the total quantity on lines of **all Draft POs** and the list of `(purchaseOrderId, number, quantity)`, in one query, same shape as `GetOnOrderByProductAsync`. Cancelled, Submitted and later POs are never included. Also used by the product page (WI-10-3) and the order review (WI-10-4).
- [ ] `PurchaseOrderService.ApplyDraftChangesAsync(IReadOnlyList<DraftLineChange>)` where `DraftLineChange(PurchaseOrderId, ProductId, DraftChangeKind Kind, int Quantity)` and `DraftChangeKind` is `Add` or `SetQuantity`. Draft only (`po.not_editable` for any other status, nothing applied); validates every change first, then applies them all in one transaction. `Add` uses the existing cost rule (supplier link price when above zero, else product cost); an `Add` for a product already on that PO, for an archived product, or with quantity below 1 is skipped with a reason. `SetQuantity` requires an existing line on that Draft (`po.line_not_found` otherwise) and quantity >= 1, and keeps the line cost. Business logic stays in the PO aggregate.
- [ ] Project numbering: `NextNumberAsync(year)` mirroring `PurchaseOrderService.NextNumberAsync` (one `UPDATE` on `DocumentSequence(Name = "Project", Year)` inside the creating transaction; format `<prefix><yyyy>-<0000>`; numbers of rolled-back creations are handed back, committed numbers are never reissued). Prefix from a new `SettingKeys.ProjectNumberPrefix` (default `PRJ-`), added to `BusinessSettings` and `SettingsService` beside `PurchaseOrderNumberPrefix`. A user-typed number (on create, or an Admin change) is accepted as is (max 30, unique) and does not consume the sequence. Changing a number never touches `DocumentSequence`, and the sequence never goes backwards.
- [ ] `MinOrderRounding.RoundUp(quantity, minOrderQuantity)` helper (quantity raised to the supplier link's minimum order quantity when one is set). Put it where F06 can reuse it (F06 FR-5 uses the same rule); do not duplicate it in the page.
- [ ] Domain events for F08 in the existing style: `ProjectCreated`, `BomChanged`, `ProjectBuilt`.
- [ ] Authorization is applied by pages (WI-10-3, WI-10-4); the services take explicit actors (`PurchaseOrderActor`, `StockContext`), never read the request.
- [ ] Tests (services, SQLite in-memory):
  - [ ] `GetInDraftByProductAsync`: sums across several drafts and suppliers, lists each PO, ignores Submitted / Cancelled / Received POs, no row for a product that is in no draft.
  - [ ] Status with drafts: a draft that covers the need gives `InDraft`; a draft that does not leaves `Short` but reports the quantity; drafts never change `toOrder` or turn a line `Ready` / `Covered`; deleting the draft returns the line to `Short`; the project state order (Short > InDraft > Covered > Ready).
  - [ ] Status math table: on hand covers, on order covers, partly covered, nothing, zero-quantity edge, units remaining after a partial build, built project, empty project, archived part flagged, Draft PO lines not counted as on order.
  - [ ] Project numbering: first project of a year is `PRJ-<year>-0001`, the next `0002`, a new year restarts, the prefix setting is honoured, a rolled-back create hands its number back, a typed number is kept and does not consume the sequence, a duplicate number in another case is a field error on `Number`, `UpdateAsync` leaves the number unchanged, `ChangeNumberAsync` works for an Admin and is refused for Staff and Viewer, refuses a duplicate, keeps BOM lines, builds and ledger references untouched, and a stale `rowVersion` is a concurrency error, search finds a project by number, two concurrent creates get different numbers.
  - [ ] Add line: duplicate product error names the line; archived product refused; remove is idempotent.
  - [ ] Quick-create: product exists in the catalog afterwards; a duplicate SKU creates neither product nor line.
  - [ ] Order shortages: **Add** lands on the chosen draft; a new draft for a supplier is created and numbered, and none is created when no decision is Add; **UpdateQuantity** sets the quantity on the named draft line and keeps its cost; **Skip** changes nothing; Add / Update / Skip mixed in one call; a part held by two drafts can be updated on either; a non-draft target or draft refused with nothing applied (all-or-nothing); an Add for a product already on that PO is skipped with a reason; archived part skipped; UpdateQuantity for a part not on that draft refused; pricing uses the supplier link, then the product; min-order rounding applied; results report added, updated (old to new) and skipped.
  - [ ] Build: all-or-nothing when one part is short (no ledger rows, no `ProjectBuild`, on-hand unchanged); success posts one `Issue` per line with the reference, updates `QuantityOnHand` through the ledger, writes `ProjectBuild`; a stock-post failure rolls back the `ProjectBuild`; the consistency check (architecture invariant 1) still passes; remaining units drop.
  - [ ] Delete: refused when builds exist, allowed otherwise.
  - [ ] `StockInvariantSourceTests` stays green (no production file other than `StockService` writes `QuantityOnHand`).

## Acceptance criteria

- [ ] A project with a BOM reports the correct per-line and per-project state for every case in the status table.
- [ ] For each short line the user's decision (Add, Update quantity, Skip) is applied exactly, to a chosen draft, a new draft for a chosen supplier, or the draft that already holds the part, with a clear added / updated / skipped result.
- [ ] A build that cannot be fully covered posts nothing and says which parts are short.
- [ ] `dotnet build` has zero warnings and `dotnet test` is green.

## Notes

- Do not write stock transactions or `Product.QuantityOnHand` directly. Do not change `StockTransaction` (decision D18).
- Keep the status calculation in a pure function over a small input record so the table test needs no database; the service only loads the data.
- Update the architecture doc (invariants: builds only post stock through `StockService`) and the F06 spec (shared min-order helper) in this change.
