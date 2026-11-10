# WI-13-2 — Conversion service, release on cancel

- **Feature:** [F13 — Requisition to purchase order](../../Features/F13-requisition-to-po.md)
- **Status:** Done
- **Depends on:** WI-13-1, WI-12-2, WI-05-2
- **Estimate:** M

## Goal

Approved lines can be sent to new or draft POs in one transaction, never twice.

## Scope

- In: to-order query, conversion, release, cancel approved, derived ordered state, tests.
- Out: screens (WI-13-3).

## Tasks

- [x] To-order query: approved lines with no link, with on hand, on order, in draft, preferred supplier. Filter by supplier.
- [x] `ConvertAsync(groups, actor)`: each group is a supplier plus target (new draft or draft PO id). One transaction.
- [x] Goes through `PurchaseOrderService`; reuse or extract what `ProjectService.AddShortagesToOrderAsync` already does for creating drafts and merging onto an existing PO line.
- [x] Re-check inside the transaction: line approved and unlinked, target still `Draft` and for that supplier, part and supplier active.
- [x] A unique-index race on `RequisitionLineId` returns a friendly error, not a 500.
- [x] `PurchaseOrderService.CancelAsync` deletes links for that PO's lines in its transaction.
- [x] `CancelApprovedAsync`: `CanApprove`, only with no linked lines.
- [x] Derived display state: Ordered, Partly ordered; filterable in search.
- [x] Publish a converted event through `IRequisitionEvents`.
- [x] Tests: new draft, existing draft, merge onto existing PO line, two requisitions onto one PO line, double conversion refused, non-draft target refused, archived part refused, rollback leaves nothing, release on line removal, draft delete and PO cancel, Viewer refused.

## Acceptance criteria

- [x] Converting the same line twice, in parallel, creates one link.
- [x] A line released from a cancelled PO can be ordered again.

## Notes

Conversion needs a `PurchaseOrderActor`; a Viewer never reaches it because the page requires `CanEdit`, and the service refuses an actor without `CanOrder` as well. It reuses `CreateDraftAsync` and `ApplyDraftChangesAsync` inside its own transaction rather than extracting code from `ProjectService`, and does not round up to minimum order quantities (warning only). Converted is published once per requisition.
