# WI-12-1 — Manager role, RequisitionLineDecision, permission settings

- **Feature:** [F12 — Requisition approvals](../../Features/F12-requisition-approvals.md)
- **Status:** Done
- **Depends on:** WI-11-1
- **Estimate:** S

## Goal

The Manager role and the table holding line decisions exist.

## Scope

- In: role seed, decision entity, permission setting keys, migration.
- Out: policies and service (WI-12-2), screens (WI-12-3).

## Tasks

- [x] `Roles.Manager`, added to `Roles.All`; seed in `ApplicationUserConfiguration` with a fixed id like the other roles.
- [x] `RequisitionLineDecision`: Id, RequisitionLineId (UQ, Cascade), Decision (`Approved | Rejected`, check), QuantityApproved, Reason, DecidedById (FK users, Restrict), DecidedAt.
- [x] Check constraint: Approved has QuantityApproved ≥ 1; Rejected has QuantityApproved = 0.
- [x] Add Manager to `DataPermissions.ConfigurableRoles`; keys `Permission.<Name>.Manager`, defaults matching Staff.
- [x] Migration `AddRequisitionApprovals` seeds the role and the four settings.
- [x] Schema tests: role present, unique decision per line, check constraints, defaults.

## Acceptance criteria

- [x] Migration applies to an empty DB and to the previous schema.
- [x] Existing users keep their roles.

## Notes

A separate table avoids a D16 rebuild of `RequisitionLines`. Added beyond the task list: `RequisitionCancellation`, because FR-7 needs somewhere to keep the reason and who cancelled.
