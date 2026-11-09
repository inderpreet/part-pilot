# WI-11-1 — Requisition, RequisitionLine schema

- **Feature:** [F11 — Requisitions](../../Features/F11-requisitions.md)
- **Status:** Done
- **Depends on:** WI-01-1, WI-02-1
- **Estimate:** S

## Goal

Tables for requisitions and their lines exist, with the constraints the service relies on.

## Scope

- In: entities, configurations, migration, number prefix setting.
- Out: services (WI-11-2), screens (WI-11-3), decisions (F12), order links (F13).

## Tasks

- [x] `Requisition`: Id, Number (UQ, NOCASE), Status (string), RequestedById (FK users, Restrict), NeededBy (`DateOnly?`), Reason, SubmittedAt?, DecidedAt?, RowVersion, timestamps.
- [x] Status check constraint with all five values from the start: `Draft, Submitted, Approved, Rejected, Cancelled` (avoids a D16 rebuild later).
- [x] `RequisitionLine`: Id, RequisitionId (Cascade, only drafts are deleted), ProductId (Restrict), QuantityRequested (check ≥ 1), Note. UQ (RequisitionId, ProductId).
- [x] Indexes: (Status, SubmittedAt) for the queue; RequestedById; ProductId.
- [x] Setting key `Requisition.NumberPrefix`, default `REQ-`, in `SettingKeys` and the migration.
- [x] Migration `AddRequisitions`; applies to an empty DB and to the previous schema.
- [x] Schema tests: tables, constraints, unique line per part, Restrict on product delete, default setting.

## Acceptance criteria

- [x] Migration applies cleanly in both directions.
- [x] A second line for the same part on one requisition is refused by the database.

## Notes

Update architecture §5 if the implemented shape differs from the plan.

Implemented with one addition: `SubmittedById` (nullable FK to users, Restrict), because FR-6 shows who submitted and an Admin can submit on someone's behalf.
