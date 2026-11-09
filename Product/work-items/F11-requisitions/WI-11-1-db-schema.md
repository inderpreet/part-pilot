# WI-11-1 — Requisition, RequisitionLine schema

- **Feature:** [F11 — Requisitions](../../Features/F11-requisitions.md)
- **Status:** Planned
- **Depends on:** WI-01-1, WI-02-1
- **Estimate:** S

## Goal

Tables for requisitions and their lines exist, with the constraints the service relies on.

## Scope

- In: entities, configurations, migration, number prefix setting.
- Out: services (WI-11-2), screens (WI-11-3), decisions (F12), order links (F13).

## Tasks

- [ ] `Requisition`: Id, Number (UQ, NOCASE), Status (string), RequestedById (FK users, Restrict), NeededBy (`DateOnly?`), Reason, SubmittedAt?, DecidedAt?, RowVersion, timestamps.
- [ ] Status check constraint with all five values from the start: `Draft, Submitted, Approved, Rejected, Cancelled` (avoids a D16 rebuild later).
- [ ] `RequisitionLine`: Id, RequisitionId (Cascade, only drafts are deleted), ProductId (Restrict), QuantityRequested (check ≥ 1), Note. UQ (RequisitionId, ProductId).
- [ ] Indexes: (Status, SubmittedAt) for the queue; RequestedById; ProductId.
- [ ] Setting key `Requisition.NumberPrefix`, default `REQ-`, in `SettingKeys` and the migration.
- [ ] Migration `AddRequisitions`; applies to an empty DB and to the previous schema.
- [ ] Schema tests: tables, constraints, unique line per part, Restrict on product delete, default setting.

## Acceptance criteria

- [ ] Migration applies cleanly in both directions.
- [ ] A second line for the same part on one requisition is refused by the database.

## Notes

Update architecture §5 if the implemented shape differs from the plan.
