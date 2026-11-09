# F12 — Requisition approvals

- **Status:** Planned
- **Depends on:** F11
- **Roles:** New **Manager** role and Admin decide. Requesters see the outcome.

## Summary

Adds a Manager role and an approval step. A Manager reviews a submitted requisition and decides **each line**: approve it, approve a
lower quantity, or reject it with a reason. Only approved quantities can be ordered ([F13](F13-requisition-to-po.md)).

## User stories

- As a Manager, I want a queue of requisitions waiting for me so that nothing is missed.
- As a Manager, I want to approve some lines, reduce others and reject the rest in one decision.
- As a requester, I want to see what was approved and why anything was reduced or rejected.
- As an Admin, I want to give someone the Manager role from user management.

## Functional requirements

1. FR-1 New role **Manager**: everything Staff can do, plus deciding requisitions. Assigned on the user edit page; one role per user as today.
2. FR-2 New policy `CanApprove` (Admin, Manager). `CanEdit` and `CanView` include Manager.
3. FR-3 Approval queue: `Submitted` requisitions, oldest first, with requester, needed-by date, line count and estimated value (from `Product.UnitCostCents`; shown only, not stored).
4. FR-4 Decision form, one row per line: on hand, on order, requested quantity, decision (Approve / Reject), approved quantity (default: requested), reason.
5. FR-5 Every line must be decided; the decisions save together in one transaction.
   Approved quantity is between 1 and the requested quantity. A reason is required for a rejection or a reduced quantity.
6. FR-6 Outcome: `Approved` when at least one line is approved, otherwise `Rejected`.
7. FR-7 A Manager or Admin can cancel a `Submitted` requisition instead of deciding it, with a reason.
8. FR-8 The nav shows a count of requisitions awaiting a decision for users who pass `CanApprove`, like the low-stock badge.
9. FR-9 The F07 data-permission matrix gains a **Manager** column. Defaults match Staff (exports allowed, imports denied).

## Business rules

- A decision is final. To change the outcome, the requester raises a new requisition.
- **Managers and Admins can decide their own requisitions.** The detail page shows when the requester decided it, so self-approval stays visible.
- Concurrency: `Requisition.RowVersion`, and the status is re-checked inside the transaction, so two Managers cannot both decide.
- A Manager cannot raise an approved quantity above the request.

## Data model

`RequisitionLineDecision`, and role `Manager` in `AspNetRoles` — see architecture §5. A separate table avoids a rebuild of `RequisitionLines` to add a foreign key (D16).

## Screens / endpoints

| Route | Purpose | Policy |
|---|---|---|
| `/Requisitions/Approvals` | Queue | CanApprove |
| `/Requisitions/{id}/Decide` | Line-by-line decision | CanApprove |
| `/Requisitions/{id}?handler=Cancel` (POST) | Cancel a submitted requisition | CanApprove |
| `/Admin/Users/Edit/{id}` | Manager appears in the role choice | RequireAdmin |
| `/Admin/Settings` | Manager column in "Data permissions" | RequireAdmin |

## Out of scope

- Approval chains, several approvers on one requisition, delegation while away.
- Approval limits by value.
- Approving purchase orders themselves (open question in F05).

## Work items

| ID | Title | Status |
|---|---|---|
| [WI-12-1](../work-items/F12-requisition-approvals/WI-12-1-db-schema.md) | Manager role, RequisitionLineDecision, permission settings | Planned |
| [WI-12-2](../work-items/F12-requisition-approvals/WI-12-2-api.md) | Manager policies, decision service | Planned |
| [WI-12-3](../work-items/F12-requisition-approvals/WI-12-3-frontend.md) | Approval queue, decision form, role and permission UI | Planned |

## Open questions

- ~~Who approves?~~ Decided 2026-11-09: Admin and Manager.
- ~~Can someone decide their own requisition?~~ Decided 2026-11-09: yes, Managers and Admins can.
